using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing.Tests.Infrastructure;

/// <summary>Models on the other engines, built against connection strings that never connect.</summary>
public static class Contexts
{
    public static ShopContext Npgsql()
        => new(new DbContextOptionsBuilder<ShopContext>().UseNpgsql("Host=unused").Options);

    public static ShopContext SqlServer()
        => new(new DbContextOptionsBuilder<ShopContext>().UseSqlServer("Server=unused").Options);

    public static ShopContext MySql()
        => new(new DbContextOptionsBuilder<ShopContext>().UseMySQL("Server=unused").Options);

    public static ShopContext Sqlite()
        => new(new DbContextOptionsBuilder<ShopContext>().UseSqlite("Data Source=:memory:").Options);
}
