using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DBConn
{
    /// <summary>
    /// 안전하고 고성능의 재사용 가능한 공통 DB 유틸리티입니다.
    /// - 연결 풀 기반의 호출 단위 생명주기 관리
    /// - 익명 객체, Dictionary, SqlParameter 파라미터 자동 바인딩 (프로퍼티 리플렉션 캐싱)
    /// - 동기/비동기, 트랜잭션, 취소 토큰, 타임아웃, 진단 로깅 훅 지원
    /// - DataSet 다중 결과 셋 조회 및 고속 대량 처리(BulkInsert, BulkMerge) 내장
    /// </summary>
    public class DBConn : IDisposable
    {
        public string ConnectionString { get; }

        // 리플렉션 비용 최적화를 위한 프로퍼티 캐시
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _propertyCache = new();

        /// <summary>
        /// DBConn 인스턴스를 초기화합니다.
        /// </summary>
        public DBConn(string connectionString)
        {
            ConnectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentNullException(nameof(connectionString));
        }

        #region === 기본 빌더 & 헬퍼 ===

        private SqlCommand CreateCommand(SqlConnection conn, string sql,
            IEnumerable<SqlParameter>? parameters, int timeoutSeconds,
            SqlTransaction? tx = null)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentNullException(nameof(sql));

            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandType = CommandType.Text;
            cmd.CommandTimeout = timeoutSeconds > 0 ? timeoutSeconds : 30;
            if (tx != null) cmd.Transaction = tx;

            if (parameters != null)
            {
                foreach (var p in parameters)
                {
                    // 파라미터 중복 소속 방지를 위한 안전한 복제 바인딩
                    cmd.Parameters.Add(p is ICloneable cloneable
                        ? (SqlParameter)cloneable.Clone()
                        : new SqlParameter(p.ParameterName, p.Value));
                }
            }

            // 진단 훅 호출
            RaiseExecuting(sql, cmd.Parameters.Cast<SqlParameter>());

            return cmd;
        }

        /// <summary>
        /// 익명 객체, Dictionary, SqlParameter 컬렉션을 안전하게 변환합니다.
        /// </summary>
        public static IEnumerable<SqlParameter>? ToSqlParameters(object? param)
        {
            if (param == null) return null;

            if (param is IEnumerable<SqlParameter> ready) return ready;

            if (param is IDictionary<string, object?> dict)
            {
                var list = new List<SqlParameter>(dict.Count);
                foreach (var kv in dict)
                {
                    var name = kv.Key.StartsWith("@") ? kv.Key : "@" + kv.Key;
                    list.Add(new SqlParameter(name, kv.Value ?? DBNull.Value));
                }
                return list;
            }

            var type = param.GetType();
            var props = _propertyCache.GetOrAdd(type, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
            var paramList = new List<SqlParameter>(props.Length);

            foreach (var pi in props)
            {
                var name = pi.Name.StartsWith("@") ? pi.Name : "@" + pi.Name;
                var value = pi.GetValue(param, null) ?? DBNull.Value;
                paramList.Add(new SqlParameter(name, value));
            }

            return paramList;
        }

        private static string EscapeSqlIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            return $"[{name.Trim().TrimStart('[').TrimEnd(']').Replace("]", "]]")}]";
        }

        private static string FormatTableName(string rawTableName)
        {
            if (string.IsNullOrWhiteSpace(rawTableName)) return rawTableName;
            var parts = rawTableName.Split('.');
            return string.Join(".", parts.Select(EscapeSqlIdentifier));
        }

        #endregion

        #region === SELECT: DataTable / DataSet ===

        public DataTable Query(string sql, object? param = null, int timeoutSeconds = 30)
        {
            using var conn = new SqlConnection(ConnectionString);
            conn.Open();

            using var cmd = CreateCommand(conn, sql, ToSqlParameters(param), timeoutSeconds);
            using var reader = cmd.ExecuteReader();
            var dt = new DataTable();
            dt.Load(reader);
            return dt;
        }

        public async Task<DataTable> QueryAsync(string sql, object? param = null, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = CreateCommand(conn, sql, ToSqlParameters(param), timeoutSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var dt = new DataTable();
            dt.Load(reader);
            return dt;
        }

        /// <summary>
        /// 세미콜론(;)으로 구분된 다중 SELECT 쿼리를 1회 왕복으로 실행하여 DataSet으로 가져옵니다. (동기)
        /// </summary>
        public DataSet QueryDataSet(string multiSql, string[]? tableNames = null, object? param = null, int timeoutSeconds = 30)
        {
            using var conn = new SqlConnection(ConnectionString);
            conn.Open();

            using var cmd = CreateCommand(conn, multiSql, ToSqlParameters(param), timeoutSeconds);
            using var reader = cmd.ExecuteReader();
            var ds = new DataSet();
            ds.Load(reader, LoadOption.OverwriteChanges, tableNames ?? Array.Empty<string>());
            return ds;
        }

        /// <summary>
        /// 세미콜론(;)으로 구분된 다중 SELECT 쿼리를 1회 왕복으로 실행하여 DataSet으로 가져옵니다. (비동기)
        /// </summary>
        public async Task<DataSet> QueryDataSetAsync(string multiSql, string[]? tableNames = null, object? param = null, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = CreateCommand(conn, multiSql, ToSqlParameters(param), timeoutSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var ds = new DataSet();
            int tableIndex = 0;

            do
            {
                var dt = new DataTable();
                if (tableNames != null && tableIndex < tableNames.Length && !string.IsNullOrWhiteSpace(tableNames[tableIndex]))
                {
                    dt.TableName = tableNames[tableIndex];
                }
                dt.Load(reader);
                ds.Tables.Add(dt);
                tableIndex++;
            } while (!reader.IsClosed && reader.NextResult());

            return ds;
        }

        #endregion

        #region === SELECT: Reader -> Mapping ===

        public List<T> QueryMap<T>(string sql, Func<SqlDataReader, T> map, object? param = null, int timeoutSeconds = 30)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));

            using var conn = new SqlConnection(ConnectionString);
            conn.Open();

            using var cmd = CreateCommand(conn, sql, ToSqlParameters(param), timeoutSeconds);
            using var reader = cmd.ExecuteReader();

            var list = new List<T>();
            while (reader.Read())
            {
                list.Add(map(reader));
            }
            return list;
        }

        public async Task<List<T>> QueryMapAsync<T>(string sql, Func<SqlDataReader, T> map, object? param = null, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));

            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = CreateCommand(conn, sql, ToSqlParameters(param), timeoutSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var list = new List<T>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(map(reader));
            }
            return list;
        }

        #endregion

        #region === SCALAR / NONQUERY ===

        public T? Scalar<T>(string sql, object? param = null, int timeoutSeconds = 30)
        {
            using var conn = new SqlConnection(ConnectionString);
            conn.Open();

            using var cmd = CreateCommand(conn, sql, ToSqlParameters(param), timeoutSeconds);
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return default;
            return (T)Convert.ChangeType(result, typeof(T));
        }

        public async Task<T?> ScalarAsync<T>(string sql, object? param = null, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = CreateCommand(conn, sql, ToSqlParameters(param), timeoutSeconds);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result == null || result == DBNull.Value) return default;
            return (T)Convert.ChangeType(result, typeof(T));
        }

        public int Execute(string sql, object? param = null, int timeoutSeconds = 30)
        {
            using var conn = new SqlConnection(ConnectionString);
            conn.Open();

            using var cmd = CreateCommand(conn, sql, ToSqlParameters(param), timeoutSeconds);
            return cmd.ExecuteNonQuery();
        }

        public async Task<int> ExecuteAsync(string sql, object? param = null, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = CreateCommand(conn, sql, ToSqlParameters(param), timeoutSeconds);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        #endregion

        #region === Transaction ===

        /// <summary>
        /// 동기 트랜잭션 작업을 실행합니다. 내부에서 커밋/롤백을 보장합니다.
        /// </summary>
        public void ExecuteInTransaction(
            Action<SqlConnection, SqlTransaction> work,
            IsolationLevel isolation = IsolationLevel.ReadCommitted,
            int timeoutSeconds = 30)
        {
            using var conn = new SqlConnection(ConnectionString);
            conn.Open();

            using var tx = conn.BeginTransaction(isolation);
            try
            {
                work(conn, tx);
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* swallow rollback exceptions */ }
                throw;
            }
        }

        /// <summary>
        /// 비동기 트랜잭션 작업을 실행합니다. 내부에서 커밋/롤백을 보장합니다.
        /// </summary>
        public async Task ExecuteInTransactionAsync(
            Func<SqlConnection, SqlTransaction, Task> work,
            IsolationLevel isolation = IsolationLevel.ReadCommitted,
            int timeoutSeconds = 30,
            CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var tx = conn.BeginTransaction(isolation);
            try
            {
                await work(conn, tx).ConfigureAwait(false);
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* swallow rollback exceptions */ }
                throw;
            }
        }

        #endregion

        #region === 대량 처리 (BULK INSERT / BULK MERGE) ===

        /// <summary>
        /// DataTable 대용량 데이터를 SqlBulkCopy를 통해 대상 테이블에 고속으로 일괄 삽입합니다. (비동기)
        /// </summary>
        public async Task BulkInsertAsync(string destinationTable, DataTable dataTable, int batchSize = 5000, int timeoutSeconds = 300, CancellationToken ct = default)
        {
            if (dataTable == null || dataTable.Rows.Count == 0) return;

            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var bulkCopy = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepIdentity, null)
            {
                DestinationTableName = FormatTableName(destinationTable),
                BatchSize = batchSize,
                BulkCopyTimeout = timeoutSeconds
            };

            foreach (DataColumn col in dataTable.Columns)
            {
                bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 임시 테이블과 MERGE 문을 활용해 대용량 데이터를 고속으로 Upsert(수정/신규삽입)합니다. (비동기)
        /// </summary>
        public async Task BulkMergeAsync(string targetTable, DataTable dataTable, string[] keyColumns, string[] updateColumns, string[]? insertColumns = null, int timeoutSeconds = 300, CancellationToken ct = default)
        {
            if (dataTable == null || dataTable.Rows.Count == 0) return;

            var formattedTargetTable = FormatTableName(targetTable);
            var tempTableName = $"#TempBulk_{Guid.NewGuid():N}";

            await ExecuteInTransactionAsync(async (conn, tx) =>
            {
                // 1. 타겟 테이블 구조를 본뜬 빈 임시 테이블(#) 생성
                var createTempSql = $"SELECT TOP 0 * INTO {tempTableName} FROM {formattedTargetTable};";
                using (var cmd = CreateCommand(conn, createTempSql, null, timeoutSeconds, tx))
                {
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // 2. 임시 테이블에 SqlBulkCopy로 고속 적재
                using (var bulkCopy = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default | SqlBulkCopyOptions.KeepIdentity, tx))
                {
                    bulkCopy.DestinationTableName = tempTableName;
                    bulkCopy.BulkCopyTimeout = timeoutSeconds;

                    foreach (DataColumn col in dataTable.Columns)
                    {
                        bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                    }

                    await bulkCopy.WriteToServerAsync(dataTable, ct).ConfigureAwait(false);
                }

                // 3. 임시 테이블 인덱스 생성 (MERGE 성능 최적화)
                var indexCols = string.Join(", ", keyColumns.Select(EscapeSqlIdentifier));
                var createIndexSql = $"CREATE CLUSTERED INDEX IX_Temp_Keys ON {tempTableName} ({indexCols});";
                using (var idxCmd = CreateCommand(conn, createIndexSql, null, timeoutSeconds, tx))
                {
                    await idxCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // 4. MERGE 구문 생성 및 실행
                var onConditions = keyColumns.Select(k => $"T.{EscapeSqlIdentifier(k)} = S.{EscapeSqlIdentifier(k)}");
                var updateAssignments = updateColumns.Select(c => $"T.{EscapeSqlIdentifier(c)} = S.{EscapeSqlIdentifier(c)}");

                var mergeSql = $@"
                    MERGE INTO {formattedTargetTable} AS T
                    USING {tempTableName} AS S
                    ON ({string.Join(" AND ", onConditions)})
                    WHEN MATCHED THEN
                        UPDATE SET {string.Join(", ", updateAssignments)}";

                if (insertColumns != null && insertColumns.Length > 0)
                {
                    var insertColsJoined = string.Join(", ", insertColumns.Select(EscapeSqlIdentifier));
                    var insertValsJoined = string.Join(", ", insertColumns.Select(c => $"S.{EscapeSqlIdentifier(c)}"));

                    mergeSql += $@"
                    WHEN NOT MATCHED BY TARGET THEN
                        INSERT ({insertColsJoined})
                        VALUES ({insertValsJoined});";
                }
                else
                {
                    mergeSql += ";";
                }

                using (var mergeCmd = CreateCommand(conn, mergeSql, null, timeoutSeconds, tx))
                {
                    await mergeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // 5. 임시 테이블 정리
                using (var dropCmd = CreateCommand(conn, $"DROP TABLE {tempTableName};", null, timeoutSeconds, tx))
                {
                    await dropCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

            }, IsolationLevel.ReadCommitted, timeoutSeconds, ct).ConfigureAwait(false);
        }

        #endregion

        #region === Diagnostics Hook ===

        /// <summary>쿼리 실행 전 호출되는 콜백(로깅, 디버깅 등).</summary>
        public Action<string, IEnumerable<SqlParameter>?>? OnExecuting { get; set; }

        private void RaiseExecuting(string sql, IEnumerable<SqlParameter>? parameters)
            => OnExecuting?.Invoke(sql, parameters);

        #endregion

        public void Dispose()
        {
            // per-call 패턴이므로 기본 해제 작업 없음 (호환성 유지용)
        }
    }
}
