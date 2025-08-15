using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("meteo", c =>
{
    c.BaseAddress = new Uri("https://api.open-meteo.com/");
    c.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();

app.UseDefaultFiles();   // чтобы index.html открывался по /
app.UseStaticFiles();

// Прокси-эндпоинт: прогноз для Киева на 7 дней + почасовой на несколько суток
app.MapGet("/api/forecast", async (IHttpClientFactory httpFactory) =>
{
    var client = httpFactory.CreateClient("meteo");

    // Координаты Киева: 50.4501, 30.5234; часовой пояс — Europe/Kyiv
    var url =
        "v1/forecast" +
        "?latitude=50.4501&longitude=30.5234" +
        "&hourly=temperature_2m,precipitation_probability,weathercode" +
        "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum" +
        "&forecast_days=7" +
        "&timezone=Europe%2FKyiv";

    try
    {
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var content = await resp.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", Encoding.UTF8);
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Ошибка запроса к Open-Meteo", detail: ex.Message, statusCode: 502);
    }
});

app.Run();
