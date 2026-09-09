# DBConn 공통 DB 모듈 가이드

C# 기반 프로젝트에서 **SQL Server**를 안전하고 고성능으로 다루기 위한 공통 DB 모듈(`DBConn`)입니다.

호출 단위 연결 관리, 파라미터 자동 바인딩, 동기/비동기 API, 트랜잭션, FarPoint Spread(DataTable) 바인딩 지원은 물론 **다중 DataSet 조회**와 고속 대량 적재(Bulk Insert / Merge)를 완벽히 지원합니다.

---

## 목차

1. [배경 및 목적](#배경-및-목적)
2. [핵심 기능](#핵심-기능)
3. [파일 구조](#파일-구조)
4. [DB 인스턴스 초기화](#db-인스턴스-초기화)
5. [단일 조회 (DataTable)](#단일-조회-datatable)
6. [다중 조회 (DataSet - 1회 왕복)](#다중-조회-dataset---1회-왕복)
7. [DTO 매핑 조회 (QueryMap)](#dto-매핑-조회-querymap)
8. [FarPoint Spread 바인딩](#farpoint-spread-바인딩)
9. [INSERT / UPDATE / DELETE](#insert--update--delete)
10. [Scalar 조회 (단일값)](#scalar-조회-단일값)
11. [트랜잭션 (동기 / 비동기)](#트랜잭션-동기--비동기)
12. [대량 처리 (Bulk Insert / Merge)](#대량-처리-bulk-insert--merge)
13. [파라미터 전달 방식](#파라미터-전달-방식)
14. [진단 및 로깅 훅 (OnExecuting)](#진단-및-로깅-훅-onexecuting)

---

## 배경 및 목적

기존 공통 모듈의 전역 `SqlConnection` 상시 오픈, 리소스 누수, 비효율적인 수동 파라미터 바인딩, 수만 건 루프 실행 시의 성능 병목을 해결하기 위해 개발되었습니다.

**개선 목표**

* **안정성:** 호출 단위 열기/닫기(`using` / `await using`)로 커넥션 풀을 극대화하여 연결 누수 원천 차단
* **생산성:** Dapper 스타일의 익명 객체/딕셔너리 자동 파라미터화 (리플렉션 캐싱 적용)
* **성능:** 다중 쿼리 1회 왕복(`DataSet`) 및 `SqlBulkCopy` 기반의 대량 데이터 고속 처리(수초 내 적재)
* **확장성:** 비동기 I/O(`async/await`), `CancellationToken`, FarPoint Spread(DataTable) 최적화

---

## 핵심 기능

* ✅ 호출 단위 자동 연결 풀링 (`await using` 기반 누수 방지)
* ✅ `Query / QueryAsync` → `DataTable` 단일 조회
* ✅ `QueryDataSet / QueryDataSetAsync` → 세미콜론(`;`) 다중 쿼리 1회 왕복 `DataSet` 조회
* ✅ `QueryMap / QueryMapAsync` → Reader 기반 고속 DTO 매핑
* ✅ `Execute / ExecuteAsync` → 영향받은 행(Row) 수 반환
* ✅ `Scalar<T> / ScalarAsync<T>` → 단일 스칼라 값 조회 및 자동 형변환
* ✅ `BulkInsertAsync` / `BulkMergeAsync` → 수만~수십만 건 고속 적재 및 병합(Upsert)
* ✅ `ExecuteInTransaction` / `ExecuteInTransactionAsync` → 안전한 트랜잭션 래퍼
* ✅ `ConcurrentDictionary` 기반 리플렉션 캐싱 (익명 객체 파라미터 바인딩 가속)
* ✅ `OnExecuting` 진단 훅 제공 (SQL 실행 전 로깅/디버깅 지원)

---

## 파일 구조

```text
/Common
└─ DBConn.cs

```

---

## DB 인스턴스 초기화

```csharp
using DBConn;

var db = new DBConn("Data Source=ip,port;Initial Catalog=db_name;UID=user_id;Pwd=password;TrustServerCertificate=True;");

```

---

## 단일 조회 (DataTable)

### 동기 조회

```csharp
var dt = db.Query(
    "SELECT * FROM dbo.Customers WHERE City = @City",
    new { City = "Seoul" }
);

```

### 비동기 조회

```csharp
var dt = await db.QueryAsync(
    "SELECT * FROM dbo.Customers WHERE City = @City",
    new { City = "Busan" }
);

```

---

## 다중 조회 (DataSet - 1회 왕복)

세미콜론(`;`)으로 여러 개의 `SELECT` 문을 결합하여 단 1회의 DB 통신으로 여러 테이블을 가져옵니다.

```csharp
string multiSql = @"
    SELECT CustomerId, CustomerName, City FROM dbo.Customers WHERE City = @City;
    SELECT OrderId, CustomerId, TotalAmount FROM dbo.Orders;
";

// 각 결과 셋에 부여할 테이블명 지정
string[] tableNames = { "Customers", "Orders" };

DataSet ds = await db.QueryDataSetAsync(multiSql, tableNames, new { City = "Seoul" });

DataTable dtCustomers = ds.Tables["Customers"];
DataTable dtOrders = ds.Tables["Orders"];

```

---

## DTO 매핑 조회 (QueryMap)

`DataTable`을 거치지 않고 엔티티/DTO 리스트로 즉시 변환하여 메모리 오버헤드를 줄입니다.

```csharp
public class CustomerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

var list = await db.QueryMapAsync(
    "SELECT CustomerId, CustomerName FROM dbo.Customers WHERE City = @City",
    reader => new CustomerDto
    {
        Id = Convert.ToInt32(reader["CustomerId"]),
        Name = reader["CustomerName"].ToString()!
    },
    new { City = "Seoul" }
);

```

---

## FarPoint Spread 바인딩

```csharp
using FarPoint.Win.Spread;

// 1. DataTable 조회
var dt = await db.QueryAsync(
    @"SELECT CustomerName, OrderCount, LastOrderDate
      FROM dbo.vw_CustomerSummary
      WHERE City = @City",
    new { City = "Seoul" }
);

// 2. Spread 바인딩
var sheet = fpSpread1.Sheets[0];
sheet.DataSource = null;
sheet.Reset();
sheet.AutoGenerateColumns = true;
sheet.DataSource = dt;

// 3. 날짜 컬럼 포맷 지정
int colDate = sheet.Columns.IndexOf("LastOrderDate");
if (colDate >= 0)
{
    sheet.Columns[colDate].CellType = new FarPoint.Win.Spread.CellType.DateTimeCellType
    {
        DateTimeFormat = FarPoint.Win.Spread.CellType.DateTimeFormat.ShortDate
    };
}

```

---

## INSERT / UPDATE / DELETE

```csharp
// INSERT
int inserted = db.Execute(
    @"INSERT INTO dbo.Customers(CustomerName, City)
      VALUES(@Name, @City)",
    new { Name = "홍길동", City = "Seoul" }
);

// UPDATE (Async)
await db.ExecuteAsync(
    @"UPDATE dbo.Customers
      SET City = @City
      WHERE CustomerId = @Id",
    new { Id = 10, City = "Busan" }
);

// DELETE
int deleted = db.Execute(
    "DELETE FROM dbo.Customers WHERE CustomerId = @Id",
    new { Id = 10 }
);

```

---

## Scalar 조회 (단일값)

```csharp
// 동기
int total = db.Scalar<int>("SELECT COUNT(*) FROM dbo.Customers");

// 비동기
string? customerName = await db.ScalarAsync<string>(
    "SELECT CustomerName FROM dbo.Customers WHERE CustomerId = @Id",
    new { Id = 1 }
);

```

---

## 트랜잭션 (동기 / 비동기)

작업 도중 예외가 발생하면 자동으로 롤백(Rollback)되고, 성공 시 자동 커밋(Commit)됩니다.

### 비동기 트랜잭션

```csharp
await db.ExecuteInTransactionAsync(async (conn, tx) =>
{
    using var cmd1 = new SqlCommand("INSERT INTO Orders(CustomerId) VALUES(@CustomerId)", conn, tx);
    cmd1.Parameters.AddWithValue("@CustomerId", 1);
    await cmd1.ExecuteNonQueryAsync();

    using var cmd2 = new SqlCommand("UPDATE Customers SET OrderCount = OrderCount + 1 WHERE CustomerId = @CustomerId", conn, tx);
    cmd2.Parameters.AddWithValue("@CustomerId", 1);
    await cmd2.ExecuteNonQueryAsync();
});

```

### 동기 트랜잭션

```csharp
db.ExecuteInTransaction((conn, tx) =>
{
    using var cmd = new SqlCommand("DELETE FROM Orders WHERE CustomerId = @Id", conn, tx);
    cmd.Parameters.AddWithValue("@Id", 1);
    cmd.ExecuteNonQuery();
});

```

---

## 대량 처리 (Bulk Insert / Merge)

수만 건 이상의 데이터를 건별 쿼리 대신 MSSQL 전용 고속 벌크 엔진(`SqlBulkCopy`)으로 처리합니다.

### 1. Bulk Insert (초고속 일괄 삽입)

```csharp
DataTable dtLarge = GetExcelData(); // 수만 건의 데이터

// dbo.Orders 테이블로 일괄 고속 적재
await db.BulkInsertAsync("dbo.Orders", dtLarge, batchSize: 5000);

```

### 2. Bulk Merge (대량 Upsert: 수정 및 신규 삽입)

임시 테이블(`#TempBulk`)에 초고속 적재 후, 대상 테이블과 PK를 매핑하여 일괄 갱신합니다.

```csharp
DataTable dtUsers = GetUserData();

// CustomerId 기준으로 일치하면 Name, City 수정 / 일치하지 않으면 신규 추가
await db.BulkMergeAsync(
    targetTable: "dbo.Customers",
    dataTable: dtUsers,
    keyColumns: new[] { "CustomerId" },
    updateColumns: new[] { "CustomerName", "City" },
    insertColumns: new[] { "CustomerId", "CustomerName", "City" }
);

```

---

## 파라미터 전달 방식

`ToSqlParameters` 변환 엔진이 내장되어 있어 세 가지 방식을 자유롭게 혼용할 수 있습니다.

### 1. 익명 객체 (권장)

```csharp
db.Query(sql, new { Id = 1, Name = "Kim" });

```

### 2. Dictionary

```csharp
db.Query(sql, new Dictionary<string, object?>
{
    ["Id"] = 1,
    ["Name"] = "Kim"
});

```

### 3. SqlParameter 컬렉션 직접 전달

```csharp
db.Query(sql, new[]
{
    new SqlParameter("@Id", 1),
    new SqlParameter("@Name", "Kim")
});

```

---

## 진단 및 로깅 훅 (OnExecuting)

쿼리 실행 직전 실제 실행될 SQL문과 바인딩된 파라미터 목록을 가로채 로그를 남길 수 있습니다.

```csharp
var db = new DBConn("connection_string");

// 로깅 훅 등록
db.OnExecuting = (sql, parameters) =>
{
    Console.WriteLine($"[SQL 실행] {sql}");
    if (parameters != null)
    {
        foreach (var p in parameters)
        {
            Console.WriteLine($"  -> {p.ParameterName} = {p.Value}");
        }
    }
};

```
