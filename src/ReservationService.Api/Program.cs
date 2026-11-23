using ReservationService.Api;
using ReservationService.Cassandra;
using ReservationService.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<PageCursor>();
builder.Services.AddSingleton(services =>
{
    var options = new CassandraOptions();
    services.GetRequiredService<IConfiguration>().GetSection("Cassandra").Bind(options);
    options.Validate();
    return options;
});
builder.Services.AddSingleton<CassandraDatabase>();
builder.Services.AddSingleton<CassandraReservationRepository>();
builder.Services.AddSingleton<IReservationRepository>(services =>
    services.GetRequiredService<CassandraReservationRepository>());
builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHealthChecks().AddCheck<CassandraHealthCheck>("cassandra");

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapControllers();
app.MapHealthChecks("/health");
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Reservation Service v1"));
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}
app.Run();

public partial class Program;
