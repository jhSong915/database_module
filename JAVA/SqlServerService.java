import com.microsoft.sqlserver.jdbc.SQLServerBulkCopy;
import com.microsoft.sqlserver.jdbc.SQLServerBulkCopyOptions;

import java.lang.reflect.Field;
import java.lang.reflect.Modifier;
import java.lang.reflect.RecordComponent;
import java.sql.*;
import java.util.*;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.function.BiConsumer;
import java.util.function.Function;

/**
 * MSSQL 공통 데이터베이스 서비스 (JDK 21+ 전용)
 * - Java Virtual Threads(가상 스레드) 기반 비동기 Non-blocking I/O
 * - Java 21 Record 및 일반 Class 파라미터 리플렉션 자동 매핑
 * - 패턴 매칭 switch 기반 초고속 Scalar 캐스팅
 * - DataSet(다중 결과 셋) 및 MSSQL 대량 고속 처리(SQLServerBulkCopy, BulkMerge)
 */
public class SqlServerService implements AutoCloseable {

    private final String url;
    private final String user;
    private final String password;

    // JDK 21 가상 스레드 실행기 (I/O 바운드 작업 최적화)
    private static final ExecutorService VIRTUAL_EXECUTOR = Executors.newVirtualThreadPerTaskExecutor();

    // 리플렉션 및 파싱 캐시
    private static final ConcurrentHashMap<Class<?>, FieldAccessor[]> FIELD_CACHE = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, ParsedSql> SQL_CACHE = new ConcurrentHashMap<>();

    private BiConsumer<String, List<Object>> onExecuting;

    public SqlServerService(String url) {
        this(url, null, null);
    }

    public SqlServerService(String url, String user, String password) {
        if (url == null || url.isBlank()) {
            throw new IllegalArgumentException("JDBC URL은 필수 입력 값입니다.");
        }
        this.url = url;
        this.user = user;
        this.password = password;
    }

    public void setOnExecuting(BiConsumer<String, List<Object>> onExecuting) {
        this.onExecuting = onExecuting;
    }

    private Connection getConnection() throws SQLException {
        if (user != null && password != null) {
            return DriverManager.getConnection(url, user, password);
        }
        return DriverManager.getConnection(url);
    }

    // region === 1. 파라미터 파서 및 JDK 21 Record/Class 매퍼 ===

    private record ParsedSql(String executableSql, List<String> paramNames) {}
    private record FieldAccessor(String name, Function<Object, Object> getter) {}

    private static ParsedSql parseNamedParameters(String sql) {
        return SQL_CACHE.computeIfAbsent(sql, rawSql -> {
            var parsed = new StringBuilder();
            var names = new ArrayList<String>();
            int length = rawSql.length();
            boolean inQuotes = false;

            for (int i = 0; i < length; i++) {
                char c = rawSql.charAt(i);
                if (c == '\'') {
                    inQuotes = !inQuotes;
                }
                if (!inQuotes && (c == '@' || c == ':')) {
                    int j = i + 1;
                    while (j < length && (Character.isLetterOrDigit(rawSql.charAt(j)) || rawSql.charAt(j) == '_')) {
                        j++;
                    }
                    String paramName = rawSql.substring(i + 1, j);
                    names.add(paramName);
                    parsed.append('?');
                    i = j - 1;
                } else {
                    parsed.append(c);
                }
            }
            return new ParsedSql(parsed.toString(), List.copyOf(names));
        });
    }

    private PreparedStatement createPreparedStatement(Connection conn, String sql, Object param, int timeoutSeconds) throws SQLException {
        ParsedSql parsedSql = parseNamedParameters(sql);
        PreparedStatement pstmt = conn.prepareStatement(parsedSql.executableSql());

        if (timeoutSeconds > 0) {
            pstmt.setQueryTimeout(timeoutSeconds);
        }

        var orderedValues = new ArrayList<>();

        if (!parsedSql.paramNames().isEmpty() && param != null) {
            Map<String, Object> paramMap = toParameterMap(param);
            int idx = 1;
            for (String name : parsedSql.paramNames()) {
                Object val = paramMap.get(name);
                pstmt.setObject(idx++, val);
                orderedValues.add(val);
            }
        }

        if (onExecuting != null) {
            onExecuting.accept(sql, orderedValues);
        }

        return pstmt;
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> toParameterMap(Object param) {
        if (param == null) return Collections.emptyMap();
        if (param instanceof Map<?, ?> map) {
            return (Map<String, Object>) map;
        }

        Class<?> clazz = param.getClass();
        FieldAccessor[] accessors = FIELD_CACHE.computeIfAbsent(clazz, c -> {
            var list = new ArrayList<FieldAccessor>();

            if (c.isRecord()) {
                for (RecordComponent rc : c.getRecordComponents()) {
                    var method = rc.getAccessor();
                    list.add(new FieldAccessor(rc.getName(), obj -> {
                        try { return method.invoke(obj); } catch (Exception e) { return null; }
                    }));
                }
            } else {
                for (Field f : c.getDeclaredFields()) {
                    if (!Modifier.isStatic(f.getModifiers())) {
                        f.setAccessible(true);
                        list.add(new FieldAccessor(f.getName(), obj -> {
                            try { return f.get(obj); } catch (Exception e) { return null; }
                        }));
                    }
                }
            }
            return list.toArray(FieldAccessor[]::new);
        });

        var map = new HashMap<String, Object>(accessors.length);
        for (FieldAccessor fa : accessors) {
            map.put(fa.name(), fa.getter().apply(param));
        }
        return map;
    }

    private static String escapeSqlIdentifier(String name) {
        if (name == null || name.isBlank()) return name;
        return "[" + name.trim().replace("[", "").replace("]", "").replace("]", "]]") + "]";
    }

    private static String formatTableName(String rawTableName) {
        if (rawTableName == null || rawTableName.isBlank()) return rawTableName;
        return String.join(".", Arrays.stream(rawTableName.split("\\.")).map(SqlServerService::escapeSqlIdentifier).toList());
    }

    // endregion

    // region === 2. SELECT 조회 (단일 / 다중 DataSet) ===

    /** 단일 SELECT 쿼리 (동기) */
    public List<Map<String, Object>> query(String sql, Object param, int timeoutSeconds) throws SQLException {
        try (Connection conn = getConnection();
             PreparedStatement pstmt = createPreparedStatement(conn, sql, param, timeoutSeconds);
             ResultSet rs = pstmt.executeQuery()) {
            return extractRows(rs);
        }
    }

    public List<Map<String, Object>> query(String sql, Object param) throws SQLException {
        return query(sql, param, 30);
    }

    /** 단일 SELECT 쿼리 (가상 스레드 기반 비동기) */
    public CompletableFuture<List<Map<String, Object>>> queryAsync(String sql, Object param, int timeoutSeconds) {
        return CompletableFuture.supplyAsync(() -> {
            try {
                return query(sql, param, timeoutSeconds);
            } catch (SQLException e) {
                throw new RuntimeException(e);
            }
        }, VIRTUAL_EXECUTOR);
    }

    /** 세미콜론(;) 다중 SELECT 쿼리 1회 통신 실행 */
    public Map<String, List<Map<String, Object>>> queryMultiple(String multiSql, String[] tableNames, Object param, int timeoutSeconds) throws SQLException {
        var result = new LinkedHashMap<String, List<Map<String, Object>>>();

        try (Connection conn = getConnection();
             PreparedStatement pstmt = createPreparedStatement(conn, multiSql, param, timeoutSeconds)) {

            boolean hasResultSet = pstmt.execute();
            int tableIdx = 0;

            while (hasResultSet) {
                try (ResultSet rs = pstmt.getResultSet()) {
                    String tableName = (tableNames != null && tableIdx < tableNames.length && tableNames[tableIdx] != null)
                            ? tableNames[tableIdx]
                            : "Table" + (tableIdx == 0 ? "" : String.valueOf(tableIdx));

                    result.put(tableName, extractRows(rs));
                    tableIdx++;
                }
                hasResultSet = pstmt.getMoreResults();
            }
        }
        return result;
    }

    public CompletableFuture<Map<String, List<Map<String, Object>>>> queryMultipleAsync(String multiSql, String[] tableNames, Object param, int timeoutSeconds) {
        return CompletableFuture.supplyAsync(() -> {
            try {
                return queryMultiple(multiSql, tableNames, param, timeoutSeconds);
            } catch (SQLException e) {
                throw new RuntimeException(e);
            }
        }, VIRTUAL_EXECUTOR);
    }

    private static List<Map<String, Object>> extractRows(ResultSet rs) throws SQLException {
        var list = new ArrayList<Map<String, Object>>();
        ResultSetMetaData meta = rs.getMetaData();
        int colCount = meta.getColumnCount();

        while (rs.next()) {
            var row = new LinkedHashMap<String, Object>(colCount);
            for (int i = 1; i <= colCount; i++) {
                row.put(meta.getColumnLabel(i), rs.getObject(i));
            }
            list.add(row);
        }
        return list;
    }

    // endregion

    // region === 3. SELECT: DTO 람다 매핑 & SCALAR (Java 21 패턴 매칭) ===

    /** DTO 매핑 조회 (동기) */
    public <T> List<T> queryMap(String sql, Function<ResultSet, T> mapper, Object param, int timeoutSeconds) throws SQLException {
        Objects.requireNonNull(mapper, "mapper must not be null");
        var list = new ArrayList<T>();

        try (Connection conn = getConnection();
             PreparedStatement pstmt = createPreparedStatement(conn, sql, param, timeoutSeconds);
             ResultSet rs = pstmt.executeQuery()) {
            while (rs.next()) {
                list.add(mapper.apply(rs));
            }
        }
        return list;
    }

    public <T> CompletableFuture<List<T>> queryMapAsync(String sql, Function<ResultSet, T> mapper, Object param, int timeoutSeconds) {
        return CompletableFuture.supplyAsync(() -> {
            try {
                return queryMap(sql, mapper, param, timeoutSeconds);
            } catch (SQLException e) {
                throw new RuntimeException(e);
            }
        }, VIRTUAL_EXECUTOR);
    }

    /** 단일 값(Scalar) 조회 (Java 21 Pattern Matching 적용) */
    @SuppressWarnings("unchecked")
    public <T> T scalar(String sql, Class<T> clazz, Object param, int timeoutSeconds) throws SQLException {
        try (Connection conn = getConnection();
             PreparedStatement pstmt = createPreparedStatement(conn, sql, param, timeoutSeconds);
             ResultSet rs = pstmt.executeQuery()) {
            if (rs.next()) {
                Object val = rs.getObject(1);
                if (val == null) return null;
                if (clazz.isInstance(val)) return (T) val;

                return switch (clazz.getSimpleName()) {
                    case "Integer" -> (T) Integer.valueOf(((Number) val).intValue());
                    case "Long" -> (T) Long.valueOf(((Number) val).longValue());
                    case "Double" -> (T) Double.valueOf(((Number) val).doubleValue());
                    case "String" -> (T) String.valueOf(val);
                    default -> (T) val;
                };
            }
            return null;
        }
    }

    public <T> CompletableFuture<T> scalarAsync(String sql, Class<T> clazz, Object param, int timeoutSeconds) {
        return CompletableFuture.supplyAsync(() -> {
            try {
                return scalar(sql, clazz, param, timeoutSeconds);
            } catch (SQLException e) {
                throw new RuntimeException(e);
            }
        }, VIRTUAL_EXECUTOR);
    }

    // endregion

    // region === 4. CUD & 트랜잭션 ===

    public int execute(String sql, Object param, int timeoutSeconds) throws SQLException {
        try (Connection conn = getConnection();
             PreparedStatement pstmt = createPreparedStatement(conn, sql, param, timeoutSeconds)) {
            return pstmt.executeUpdate();
        }
    }

    public int execute(String sql, Object param) throws SQLException {
        return execute(sql, param, 30);
    }

    public CompletableFuture<Integer> executeAsync(String sql, Object param, int timeoutSeconds) {
        return CompletableFuture.supplyAsync(() -> {
            try {
                return execute(sql, param, timeoutSeconds);
            } catch (SQLException e) {
                throw new RuntimeException(e);
            }
        }, VIRTUAL_EXECUTOR);
    }

    @FunctionalInterface
    public interface SqlConsumer<T> {
        void accept(T t) throws Exception;
    }

    /** 트랜잭션 래퍼 (동기) */
    public void executeInTransaction(SqlConsumer<Connection> work, int isolationLevel) throws Exception {
        try (Connection conn = getConnection()) {
            conn.setAutoCommit(false);
            if (isolationLevel > 0) conn.setTransactionIsolation(isolationLevel);

            try {
                work.accept(conn);
                conn.commit();
            } catch (Exception e) {
                try { conn.rollback(); } catch (SQLException ignored) {}
                throw e;
            } finally {
                conn.setAutoCommit(true);
            }
        }
    }

    /** 트랜잭션 래퍼 (가상 스레드 비동기) */
    public CompletableFuture<Void> executeInTransactionAsync(SqlConsumer<Connection> work, int isolationLevel) {
        return CompletableFuture.runAsync(() -> {
            try {
                executeInTransaction(work, isolationLevel);
            } catch (Exception e) {
                throw new RuntimeException(e);
            }
        }, VIRTUAL_EXECUTOR);
    }

    // endregion

    // region === 5. MSSQL 고속 대량 처리 (Bulk Insert & Bulk Merge) ===

    /** ResultSet 데이터소스를 통한 고속 대량 삽입 */
    public void bulkInsert(String destinationTable, ResultSet sourceRs, int batchSize, int timeoutSeconds) throws SQLException {
        try (Connection conn = getConnection()) {
            var bulkCopy = new SQLServerBulkCopy(conn);
            var options = new SQLServerBulkCopyOptions();
            options.setTableLock(true);
            options.setCheckConstraints(true);
            options.setKeepIdentity(true);
            if (batchSize > 0) options.setBatchSize(batchSize);
            if (timeoutSeconds > 0) options.setBulkCopyTimeout(timeoutSeconds);

            bulkCopy.setBulkCopyOptions(options);
            bulkCopy.setDestinationTableName(formatTableName(destinationTable));
            bulkCopy.writeToServer(sourceRs);
        }
    }

    /** 임시 테이블 및 MERGE 문을 결합한 고속 대량 Upsert */
    public void bulkMerge(String targetTable, ResultSet sourceRs, String[] keyColumns, String[] updateColumns, String[] insertColumns, int timeoutSeconds) throws Exception {
        String formattedTarget = formatTableName(targetTable);
        String tempTableName = "#TempBulk_" + UUID.randomUUID().toString().replace("-", "");

        executeInTransaction(conn -> {
            // 1. 임시 테이블 생성
            String createTempSql = "SELECT TOP 0 * INTO " + tempTableName + " FROM " + formattedTarget + ";";
            try (Statement stmt = conn.createStatement()) {
                stmt.execute(createTempSql);
            }

            // 2. 임시 테이블 벌크 카피
            var bulkCopy = new SQLServerBulkCopy(conn);
            var options = new SQLServerBulkCopyOptions();
            options.setKeepIdentity(true);
            if (timeoutSeconds > 0) options.setBulkCopyTimeout(timeoutSeconds);
            bulkCopy.setBulkCopyOptions(options);
            bulkCopy.setDestinationTableName(tempTableName);
            bulkCopy.writeToServer(sourceRs);

            // 3. 인덱스 생성
            var escapedKeys = Arrays.stream(keyColumns).map(SqlServerService::escapeSqlIdentifier).toList();
            String idxSql = "CREATE CLUSTERED INDEX IX_Temp_Keys ON " + tempTableName + " (" + String.join(", ", escapedKeys) + ");";
            try (Statement stmt = conn.createStatement()) {
                stmt.execute(idxSql);
            }

            // 4. MERGE 문 생성 및 실행
            var onClause = String.join(" AND ", Arrays.stream(keyColumns)
                    .map(k -> "T." + escapeSqlIdentifier(k) + " = S." + escapeSqlIdentifier(k)).toList());
            var updateClause = String.join(", ", Arrays.stream(updateColumns)
                    .map(c -> "T." + escapeSqlIdentifier(c) + " = S." + escapeSqlIdentifier(c)).toList());

            var mergeSql = new StringBuilder("""
                MERGE INTO %s AS T
                USING %s AS S
                ON (%s)
                WHEN MATCHED THEN
                    UPDATE SET %s
            """.formatted(formattedTarget, tempTableName, onClause, updateClause));

            if (insertColumns != null && insertColumns.length > 0) {
                var insCols = String.join(", ", Arrays.stream(insertColumns).map(SqlServerService::escapeSqlIdentifier).toList());
                var srcCols = String.join(", ", Arrays.stream(insertColumns).map(c -> "S." + escapeSqlIdentifier(c)).toList());
                mergeSql.append("""
                    WHEN NOT MATCHED BY TARGET THEN
                        INSERT (%s)
                        VALUES (%s);
                """.formatted(insCols, srcCols));
            } else {
                mergeSql.append(";");
            }

            try (Statement stmt = conn.createStatement()) {
                stmt.execute(mergeSql.toString());
                stmt.execute("DROP TABLE " + tempTableName + ";");
            }

        }, Connection.TRANSACTION_READ_COMMITTED);
    }

    // endregion

    @Override
    public void close() {
        // per-call 패턴이므로 연결 풀 리소스 유지
    }
}
