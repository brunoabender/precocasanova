using System.Text.Json;
using System.Collections.Concurrent;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton<ProdutoService>();

        var app = builder.Build();

        app.UseSwagger();
        app.UseSwaggerUI();

        var produtoService = app.Services.GetRequiredService<ProdutoService>();

        app.MapPost("/produtos", (ProdutoRequest req) =>
        {
            produtoService.Adicionar(req.Nome);
            return Results.Ok("Produto cadastrado");
        });

        app.MapGet("/produtos", () => produtoService.Listar());

        app.MapGet("/precos", async () =>
        {
            var resultados = new List<PrecoGoogle>();
            var http = new HttpClient();
            var apiKey = "585f17ae40b08ef777d98cdec97aa71db95e706694d01f1c6f5294bf10f00a1d";

            foreach (var nome in produtoService.Listar())
            {
                var query = Uri.EscapeDataString(nome);
                var url = $"https://serpapi.com/search.json?q={query}&engine=google_shopping&api_key={apiKey}";

                var response = await http.GetStringAsync(url);
                var json = JsonDocument.Parse(response);

                if (json.RootElement.TryGetProperty("shopping_results", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        string title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                        string price = item.TryGetProperty("price", out var priceProp) ? priceProp.GetString() ?? "" : "";
                        string source = item.TryGetProperty("source", out var sourceProp) ? sourceProp.GetString() ?? "" : "";
                        string link = item.TryGetProperty("link", out var linkProp) ? linkProp.GetString() ?? "" : "";

                        resultados.Add(new PrecoGoogle(title, price, source, link));
                    }
                }
            }

            return resultados;
        });

        app.Run();
    }
}

record ProdutoRequest(string Nome);
record PrecoGoogle(string Produto, string Preco, string Loja, string Link);

class ProdutoService
{
    private readonly ConcurrentBag<string> _produtos = new();

    public void Adicionar(string nome)
    {
        if (!string.IsNullOrWhiteSpace(nome))
            _produtos.Add(nome);
    }

    public List<string> Listar() => _produtos.ToList();
}
