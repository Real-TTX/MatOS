using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatOS.Services.Data;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without starting the service (which would bind the edge and DNS ports).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ServicesDbContext>
{
    public ServicesDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ServicesDbContext>().UseSqlite("Data Source=design.db").Options);
}
