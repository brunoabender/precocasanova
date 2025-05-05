using System.Text.Json;
using System.Text.RegularExpressions;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddApplicationInsightsTelemetry();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton<ProdutoService>();

        var app = builder.Build();

        app.UseSwagger();
        app.UseSwaggerUI();

        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        var produtoService = app.Services.GetRequiredService<ProdutoService>();

        app.MapPost("/produtos", (ProdutoRequest req) =>
        {
            produtoService.Adicionar(req.Nome, req.Categoria);
            logger.LogInformation("Produto cadastrado: {Produto}", req.Nome);
            return Results.Ok("Produto cadastrado");
        });

        app.MapGet("/produtos", () =>
        {
            var produtos = produtoService.Listar();
            logger.LogInformation("Listando {Count} produtos", produtos.Count);
            return produtos;
        });

        // Retorna o melhor preço por produto
        app.MapGet("/precos", async () =>
        {
            var http = new HttpClient();
            var apiKey = "585f17ae40b08ef777d98cdec97aa71db95e706694d01f1c6f5294bf10f00a1d";
            var melhores = new List<PrecoGoogle>();

            foreach (var itemDicionario in produtoService.Listar())
            {
                var query = Uri.EscapeDataString(Sanitize(itemDicionario.Key));
                var url = $"https://serpapi.com/search.json?q={query}&engine=google_shopping&gl=br&hl=pt-BR&api_key={apiKey}" + $"{(string.IsNullOrEmpty(itemDicionario.Value) ? "" : $"&tbs=cat:{itemDicionario.Value}")}";

                try
                {
                    logger.LogInformation("Buscando preços para {Produto} na URL {Url}", itemDicionario.Key, url);

                    var response = await http.GetStringAsync(url);
                    var json = JsonDocument.Parse(response);

                    if (json.RootElement.TryGetProperty("shopping_results", out var items))
                    {
                        var precos = new List<PrecoGoogle>();

                        foreach (var item in items.EnumerateArray())
                        {
                            string title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                            string priceStr = item.TryGetProperty("price", out var priceProp) ? priceProp.GetString() ?? "" : "";
                            string source = item.TryGetProperty("source", out var sourceProp) ? sourceProp.GetString() ?? "" : "";
                            string link = item.TryGetProperty("link", out var linkProp) ? linkProp.GetString() ?? "" : "";

                            if (TryParsePrice(priceStr, out decimal preco))
                            {
                                precos.Add(new PrecoGoogle(itemDicionario.Key, preco, source, link));
                            }
                            else
                            {
                                logger.LogWarning("Preço inválido ignorado: '{PrecoTexto}' para produto {Produto}", priceStr, itemDicionario.Key);
                            }
                        }

                        var melhor = precos.OrderBy(p => p.Preco).FirstOrDefault();
                        if (melhor is not null)
                        {
                            melhores.Add(melhor);
                            logger.LogInformation("Melhor preço para {Produto}: {Preco} - {Loja}", itemDicionario.Key, melhor.Preco, melhor.Loja);
                        }
                        else
                        {
                            logger.LogWarning("Nenhum preço válido encontrado para {Produto}", itemDicionario.Key);
                        }
                    }
                    else
                    {
                        logger.LogWarning("Nenhum resultado encontrado para {Produto}", itemDicionario.Key);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erro ao buscar preços para {Produto}", itemDicionario.Key);
                }
            }

            return Results.Json(melhores);
        });


        // Retorna todos os preços coletados em JSON
        app.MapGet("/precos/todos", async () =>
        {
            var http = new HttpClient();
            var apiKey = "585f17ae40b08ef777d98cdec97aa71db95e706694d01f1c6f5294bf10f00a1d";
            var todos = new List<PrecoGoogle>();

            foreach (var itemDicionario in produtoService.Listar())
            {
                var query = Uri.EscapeDataString(Sanitize(itemDicionario.Key));
                var url = $"https://serpapi.com/search.json?q={query}&engine=google_shopping&gl=br&hl=pt-BR&api_key={apiKey}" +$"{(string.IsNullOrEmpty(itemDicionario.Value) ? "" : $"&tbs=cat:{itemDicionario.Value}")}";

                try
                {
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

                            if (TryParsePrice(price, out decimal preco))
                            {
                                todos.Add(new PrecoGoogle(title, preco, source, link));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erro ao buscar todos os preços para {Produto}", itemDicionario.Key);
                }
            }

            return Results.Json(todos);
        });

        app.Run();
    }

    static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var normalized = input
            .Replace('\u00A0', ' ')
            .Replace('\u200B', ' ')
            .Replace('\u200C', ' ')
            .Replace('\u200D', ' ')
            .Replace('\uFEFF', ' ');

        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }
    static bool TryParsePrice(string raw, out decimal preco)
    {
        preco = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;

            // Remove "R$", espaços e normaliza separadores
            var clean = raw
                .Replace("R$", "")
                .Trim();

            // Detecta e converte corretamente os formatos brasileiros
            // Ex: "1.709,05" → "1709.05"
            if (clean.Contains(','))
            {
                // Assume que ponto é separador de milhar → remove
                clean = clean.Replace(".", "");
                // Vírgula é separador decimal → troca por ponto
                clean = clean.Replace(",", ".");
            }

            return decimal.TryParse(
                clean,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out preco);
        }
        catch
        {
            return false;
        }
    }
}

record ProdutoRequest(string Nome, string? Categoria);
record PrecoGoogle(string Produto, decimal Preco, string Loja, string Link)
{
    public string PrecoFormatado => Preco.ToString("C", new System.Globalization.CultureInfo("pt-BR"));
}

class ProdutoService
{

    //[ ["Midea CFBD42 Dual Freezone 4 bocas", "2626"], ["Lava-louças 14 Serviços Electrolux LL14X Cor Inox", ""], ["churrasqueira a gás cooktop de inox felesa", ""] ];
    private readonly Dictionary<string, string> _produtos = new()
    {
        { "Midea CFBD42 Dual Freezone 4 bocas", "2626" },
        { "Lava-louças 14 Serviços Electrolux LL14X Cor Inox", "" },
        { "churrasqueira a gás cooktop de inox felesa", "" }
    };

    public void Adicionar(string nome, string categoriaId)
    {
        if (!string.IsNullOrWhiteSpace(nome))
            _produtos.Add(nome, categoriaId);
    }

    public Dictionary<string,string> Listar() => _produtos;
}
