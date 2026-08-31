var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Full kernel bootstrap (Serilog, Swagger, JWT, CORS, DbContext, etc.) lands with the
// Identity capability (task 2.x) once Synap.Application/Synap.Infrastructure have something
// to wire up. This is the minimal buildable scaffold for task 1.1.

builder.Services.AddControllers();

var app = builder.Build();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
