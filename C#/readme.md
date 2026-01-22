
# DBConn 공통 DB 모듈 가이드

C# 기반 프로젝트에서 **SQL Server**를 안전하고 일관되게 사용하는 공통 DB 모듈(`DBConn`)입니다.  
연결/해제 자동 관리, 파라미터화 쿼리, 동기/비동기, 트랜잭션, DataTable 반환(Spread.NET 바인딩 최적화)을 제공합니다.

---

## 목차

1. [배경 및 목적](#배경-및-목적)
2. [핵심 기능](#핵심-기능)
3. [파일 구조](#파일-구조)
4. [DB 연결](#db-연결)
5. [동기 조회](#동기-조회)
6. [비동기 조회](#비동기-조회)
7. [FarPoint Spread 바인딩](#farpoint-spread-바인딩)
8. [INSERT / UPDATE / DELETE](#insert--update--delete)
9. [scalar 조회](#scalar-조회)
10. [트랜잭션](#트랜잭션)
11. [파라미터 전달 방식](#파라미터-전달-방식)
    - [익명 객체](#익명-객체)
    - [Dictionary](#dictionary)
    - [SqlParameter 직접](#sqlparameter-직접)

---

## 배경 및 목적

기존 공통 DB 모듈은 전역 `SqlConnection` 2개를 상시 오픈하고 `SqlDataReader`/`SqlCommand`를 부정확하게 다루며 예외를 삼키는 문제가 있었습니다.  
이를 개선하여 **안전성**, **확장성**, **테스트 용이성**을 확보한 모듈로 리팩터링합니다.

**목표**
- 연결/해제 자동 관리(연결 풀 활용)로 안정성 향상
- 파라미터화 쿼리 기본 제공 → SQL Injection 방지
- DataTable/Scalar/Execute/Reader-Map 등 범용 API 제공
- 동기/비동기/트랜잭션 지원
- FarPoint Spread(DataTable) 바인딩 최적화

---

## 핵심 기능

- ✅ 호출 단위로 `SqlConnection`을 열고 닫음 (연결 풀 자동 활용)
- ✅ `Query / QueryAsync` → `DataTable` 반환
- ✅ `Execute / ExecuteAsync` → 영향 행수 반환
- ✅ `Scalar<T>` → 단일 값 조회
- ✅ `QueryMap / QueryMapAsync` → Reader → DTO 매핑
- ✅ 트랜잭션 래퍼 (`ExecuteInTransactionAsync`)
- ✅ 익명 객체 / `Dictionary` / `SqlParameter[]` 파라미터 지원
- ✅ 타임아웃/취소 토큰(Async 시) 확장 용이

---

## 파일 구조
/Common
└─ DBConn.cs

## db 연결
``` cssharp
using DBConn;

var db = new DBConn.DBConn(
    "Data Source=ip,port;Initial Catalog=db_name;UID=user_id;Pwd=password;"
);
```
## 동기 조회
```
var dt = db.Query(
    "SELECT * FROM dbo.Customers WHERE City = @City",
    new { City = "Seoul" }
);
```
## 비동기 조회
``` cssharp
var dt = await db.QueryAsync(
    "SELECT * FROM dbo.Customers WHERE City = @City",
    new { City = "Busan" }
);
```
### FarPoint Spread 바인딩
``` cssharp
using FarPoint.Win.Spread;

// DataTable 조회
var dt = await db.QueryAsync(
    @"SELECT CustomerName, OrderCount, LastOrderDate
      FROM dbo.vw_CustomerSummary
      WHERE City = @City",
    new { City = "Seoul" }
);

// Spread 바인딩
var sheet = fpSpread1.Sheets[0];
sheet.DataSource = null;
sheet.Reset();
sheet.AutoGenerateColumns = true;
sheet.DataSource = dt;

// 날짜 컬럼 포맷
int colDate = sheet.Columns.IndexOf("LastOrderDate");
if (colDate >= 0)
{
    sheet.Columns[colDate].CellType =
        new FarPoint.Win.Spread.CellType.DateTimeCellType
        {
            DateTimeFormat =
                FarPoint.Win.Spread.CellType.DateTimeFormat.ShortDate
        };
}
```
### INSERT / UPDATE / DELETE
``` cssharp
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
### Scalar 조회
``` cssharp
int total = db.Scalar<int>("SELECT COUNT(*) FROM dbo.Customers");
```
### 트랜잭션
``` cssharp
await db.ExecuteInTransactionAsync(async (conn, tx) =>
{
    var insert = new SqlCommand(
        "INSERT INTO Orders(CustomerId) VALUES(@CustomerId)", conn, tx);
    insert.Parameters.AddWithValue("@CustomerId", 1);
    await insert.ExecuteNonQueryAsync();

    var update = new SqlCommand(
        "UPDATE Customers SET OrderCount = OrderCount + 1 WHERE CustomerId = @CustomerId",
        conn, tx);
    update.Parameters.AddWithValue("@CustomerId", 1);
    await update.ExecuteNonQueryAsync();
});
```
### 익명 객체
``` cssharp
db.Query(sql, new { Id = 1, Name = "Kim" });
```
### Dictionary
``` cssharp
db.Query(sql, new Dictionary<string, object?>
{
    ["Id"] = 1,
    ["Name"] = "Kim"
});
```
### SqlParameter 직접
``` cssharp
db.Query(
    sql,
    new[]
    {
        new SqlParameter("@Id", 1),
        new SqlParameter("@Name", "Kim")
    }
);
```
