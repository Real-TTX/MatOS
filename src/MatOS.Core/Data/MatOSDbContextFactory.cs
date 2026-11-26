using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatOS.Core.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (<c>dotnet ef migrations …</c>).
/// It builds the context against a throwaway SQLite file and a system user accessor,
/// so migrations can be created without booting the full web host.
/// </summary>
public sealed class MatOSDbContextFactory : IDesignTimeDbContextFactory<MatOSDbContext>
{
    public MatOSDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MatOSDbContext>()
            .UseSqlite("Data Source=matos-design.db")
            .Options;

        return new MatOSDbContext(options, new SystemCurrentUserAccessor());
    }
}
