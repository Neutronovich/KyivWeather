using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddDbContext<AppDb>(o => o.UseInMemoryDatabase("weatherdb"));


builder.Services.AddHttpClient("meteo", c =>
{
    c.BaseAddress = new Uri("https://api.open-meteo.com/");
    c.Timeout = TimeSpan.FromSeconds(15);
});


builder.Services.AddCors(opt =>
{
    opt.AddPolicy("PublicReadOnly", p =>
        p.AllowAnyOrigin()           
         .WithMethods("GET", "OPTIONS")
         .AllowAnyHeader());
});

var app = builder.Build();

app.UseDefaultFiles();   
app.UseStaticFiles();
app.UseCors("PublicReadOnly");


using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();

    if (!await db.HourSamples.AnyAsync())
    {
        try
        {
            var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("meteo");
            var url =
                "v1/forecast" +
                "?latitude=50.4501&longitude=30.5234" +
                "&hourly=temperature_2m,precipitation_probability" +
                "&forecast_days=7" +
                "&timezone=Europe%2FKyiv";

            var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            using var s = await resp.Content.ReadAsStreamAsync();
            var json = await JsonDocument.ParseAsync(s);

            var hours = json.RootElement.GetProperty("hourly").GetProperty("time").EnumerateArray().ToArray();
            var temps = json.RootElement.GetProperty("hourly").GetProperty("temperature_2m").EnumerateArray().ToArray();
            var probsOk = json.RootElement.GetProperty("hourly").TryGetProperty("precipitation_probability", out var probsNode);
            var probs = probsOk ? probsNode.EnumerateArray().ToArray() : Array.Empty<JsonElement>();

            var len = Math.Min(hours.Length, temps.Length);
            if (probsOk) len = Math.Min(len, probs.Length);
            len = Math.Min(len, 72); 

            var items = new List<HourSample>(len);
            for (int i = 0; i < len; i++)
            {
                var t = DateTime.Parse(hours[i].GetString()!, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
                var temp = temps[i].GetDouble();
                var prob = (probsOk ? (probs[i].ValueKind == JsonValueKind.Null ? 0 : probs[i].GetInt32()) : 0);

                items.Add(new HourSample
                {
                    TimeUtc = t,
                    TemperatureC = temp,
                    PrecipProb = Math.Clamp(prob, 0, 100)
                });
            }

            db.HourSamples.AddRange(items);
            await db.SaveChangesAsync();
        }
        catch
        {
            
        }
    }
}


app.MapGet("/api/forecast", async (AppDb db) =>
{
    var all = await db.HourSamples.OrderBy(x => x.TimeUtc).ToListAsync();

   
    var hourly = new
    {
        time = all.Select(x => x.TimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")).ToArray(),
        temperature_2m = all.Select(x => Math.Round(x.TemperatureC, 1)).ToArray(),
        precipitation_probability = all.Select(x => x.PrecipProb).ToArray()
    };

  
    var byDay = all
        .GroupBy(x => x.TimeUtc.ToLocalTime().Date)
        .OrderBy(g => g.Key)
        .Select(g => new
        {
            date = g.Key,
            tmin = g.Min(s => s.TemperatureC),
            tmax = g.Max(s => s.TemperatureC),
            precip_sum = 0.0
        }).ToList();

    var daily = new
    {
        time = byDay.Select(d => d.date.ToString("yyyy-MM-dd")).ToArray(),
        temperature_2m_min = byDay.Select(d => Math.Round(d.tmin, 1)).ToArray(),
        temperature_2m_max = byDay.Select(d => Math.Round(d.tmax, 1)).ToArray(),
        precipitation_sum = byDay.Select(d => d.precip_sum).ToArray()
    };

    return Results.Json(new { hourly, daily });
});


app.MapPost("/api/samples", async (AppDb db, CreateHourSample dto) =>
{
    if (dto == null || dto.Time == default) return Results.BadRequest("Time is required");
    var entity = new HourSample
    {
        TimeUtc = dto.Time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dto.Time, DateTimeKind.Utc) : dto.Time.ToUniversalTime(),
        TemperatureC = dto.TemperatureC,
        PrecipProb = Math.Clamp(dto.PrecipProb, 0, 100)
    };
    db.HourSamples.Add(entity);
    await db.SaveChangesAsync();
    return Results.Created($"/api/samples/{entity.Id}", new { entity.Id });
});


app.MapDelete("/api/samples", async (AppDb db) =>
{
    db.HourSamples.RemoveRange(db.HourSamples);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();



public class HourSample
{
    public int Id { get; set; }
    public DateTime TimeUtc { get; set; }     
    public double TemperatureC { get; set; } 
    public int PrecipProb { get; set; }       
}

public class CreateHourSample
{
    public DateTime Time { get; set; }       
    public double TemperatureC { get; set; }
    public int PrecipProb { get; set; }
}

public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }
    public DbSet<HourSample> HourSamples => Set<HourSample>();
}
