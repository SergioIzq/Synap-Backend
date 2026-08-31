using System.Data;

namespace Synap.Shared.Application.Interfaces;

/// <summary>Used by read repositories that query with Dapper directly (reporting/search-style
/// reads) instead of going through EF Core - see design.md Decision 7 amendment.</summary>
public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
