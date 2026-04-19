using Dapper;
using MarketData.Core.Interfaces;
using MarketData.Core.Models;
using Microsoft.Data.SqlClient;

namespace MarketData.Infrastructure.Repositories;

public class SqlTickRepository : ITickRepository
{
    private readonly string _connectionString;
    public SqlTickRepository(string connectionString) => _connectionString = connectionString;

    public async Task AddTicksBatchAsync(IEnumerable<Tick> ticks)
    {
        using var conn = new SqlConnection(_connectionString);
        const string sql = "INSERT INTO Ticks (Exchange, Symbol, Price, Volume, Timestamp) VALUES (@Exchange, @Symbol, @Price, @Volume, @Timestamp)";
        await conn.ExecuteAsync(sql, ticks);
    }
}
