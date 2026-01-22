
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DBConn
{
    /// <summary>
    /// 안전하고 재사용 가능한 공통 DB 유틸리티.
    /// - 연결은 호출 단위로 열고 닫습니다(연결 풀 사용).
    /// - 모든 메서드는 매개변수화된 쿼리를 지원합니다.
    /// - 동기/비동기, 트랜잭션, 취소 토큰, 타임아웃 지원.
    /// </summary>
    public class DBConn : IDisposable
    {
        public string ConnectionString { get; }

        /// <summary>
        /// DBConn 인스턴스를 초기화합니다.
        /// </summary>
        public DBConn(string connectionString)
        {
            ConnectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentNullException(nameof(connectionString));
        }

        #region === 기본 빌더 ===

        private static SqlCommand CreateCommand(SqlConnection conn, string sql,
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
                    cmd.Parameters.Add(p);
            }
            return cmd;
        }

        /// <summary>
        /// 익명객체/Dictionary를 SqlParameter 컬렉션으로 변환.
        /// </summary>
        public static IEnumerable<SqlParameter>? ToSqlParameters(object? param)
        {
            if (param == null) return null;

            if (param is IEnumerable<SqlParameter> ready) return ready;

            if (param is IDictionary<string, object?> dict)
            {
                foreach (var kv in dict)
                {
                    var name = kv.Key.StartsWith("@") ? kv.Key : "@" + kv.Key;
                    yield return new SqlParameter(name, kv.Value ?? DBNull.Value);
                }
                yield break;
            }

            var type = param.GetType();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var pi in props)
            {
                var name = pi.Name.StartsWith("@") ? pi.Name : "@" + pi.Name;
                var value = pi.GetValue(param, null) ?? DBNull.Value;
                yield return new SqlParameter(name, value);
            }
        }

        #endregion

        #region === SELECT: DataTable ===

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
        /// 트랜잭션 작업을 실행합니다. 내부에서 연결/트랜잭션을 열고 커밋/롤백을 처리합니다.
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
                try { tx.Rollback(); } catch { /* swallow rollback errors */ }
                throw;
            }
        }

        #endregion

        #region === Diagnostics Hook (옵션) ===

        /// <summary>쿼리 실행 전 호출되는 콜백(로깅 등).</summary>
        public Action<string, IEnumerable<SqlParameter>?>? OnExecuting { get; set; }

        private void RaiseExecuting(string sql, IEnumerable<SqlParameter>? parameters)
            => OnExecuting?.Invoke(sql, parameters);

        #endregion

        public void Dispose()
        {
            // 현재 구조에서는 per-call로 연결을 열고 닫으므로 유지할 상태가 없습니다.
            // 향후 연결/풀, 캐시 리소스 등을 들고 있을 경우 여기에 정리 코드를 추가하세요.
        }
    }
}
