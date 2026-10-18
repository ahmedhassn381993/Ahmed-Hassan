using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Enppi.Workload.Api.Data;

/// <summary>Used only by "dotnet ef": migrations target Azure SQL / SQL Server (SQLite development databases are created
/// directly from the model). No database connection is opened when a migration or script is generated.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<WorkloadDbContext>
{
    public WorkloadDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WorkloadDbContext>()
            .UseSqlServer("Server=localhost;Database=workload;Integrated Security=true;TrustServerCertificate=true").Options);
}
