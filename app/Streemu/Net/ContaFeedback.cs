using System.Net;
using System.Text.Json.Nodes;
using Streemu.Telemetria;

namespace Streemu.Net;

/// <summary>
/// O feedback da aba da Mumu (spec 031, D5): regras puras do que sai do PC. O corpo do <c>POST /feedback</c> tem só <c>texto</c>, <c>versao</c> (a do app) e
/// <c>comNome</c> (se a pessoa marcou "Mandar com meu nome"). Quem diz a conta e o nome é o servidor, pelo token; a data também é dele. Nunca vão IP,
/// nome do PC, nome de usuário do Windows nem caminho de pasta: o texto passa pela <see cref="Higiene"/> (a mesma da telemetria) antes de sair.
/// </summary>
public static class FeedbackDaMumu
{
    public const int TextoMax = 1000;

    public const string Enviado = "Recebido! A Mumu agradece e a equipe vai ler.";
    public const string DizOQueVai = "O que sai do seu PC: só o texto, a versão do Streemu e a data. Com a caixa marcada, também o nome da sua conta; sem ela, ninguém sabe quem mandou. "
        + "Nunca vai IP, nome do PC nem do seu usuário do Windows, e caminhos de pasta e endereços de IP escritos no texto são trocados por marcas.";

    /// <summary>O texto sem espaços nas pontas, limpo pela Higiene e com no máximo <see cref="TextoMax"/> caracteres (sem cortar um par substituto ao meio). Vazio vira nulo.</summary>
    public static string? LimparTexto(string? texto)
    {
        var t = (texto ?? "").Trim();
        if (t.Length == 0) return null;
        t = Higiene.Limpar(t).Trim();
        if (t.Length > TextoMax)
        {
            int fim = TextoMax;
            if (char.IsHighSurrogate(t[fim - 1])) fim--;
            t = t[..fim].TrimEnd();
        }
        return t.Length == 0 ? null : t;
    }

    /// <summary>O corpo do <c>POST /feedback</c>, ou nulo se não há texto. Só estes três campos; qualquer campo novo aqui é decisão de privacidade (spec 031, D5).</summary>
    public static JsonObject? MontarCorpo(string? texto, bool comNome, string versao)
    {
        if (LimparTexto(texto) is not { } t) return null;
        return new JsonObject { ["texto"] = t, ["versao"] = versao, ["comNome"] = comNome };
    }

    /// <summary>Código do servidor (<c>erro</c>) para a frase da aba: motivo provável e próxima ação, sem janela de erro. O texto do servidor serve de reserva.</summary>
    public static string Frase(string? erro, string? textoServidor = null) => erro switch
    {
        "token_recusado" or "conta_invalida" => Conta.FraseSessaoEncerrada,
        "ritmo" or "requisicao_invalida" when !string.IsNullOrWhiteSpace(textoServidor) => textoServidor,
        "ritmo" => "Você mandou recados demais por agora. Tente de novo mais tarde.",
        "requisicao_invalida" => "Escreva sua mensagem (até 1000 letras) e tente de novo.",
        _ => "Não deu para mandar agora. Seu texto continua aqui: tente de novo em instantes.",
    };
}

public sealed partial class Conta
{
    /// <summary>
    /// Manda o feedback (<c>POST /feedback</c>, com o token da conta). O texto passa pela <see cref="FeedbackDaMumu"/> e <b>nunca vai para o log</b>. 401 renova o
    /// token e tenta uma vez mais; sem rede, sem conta ou com erro do servidor, devolve o motivo em português e a próxima ação (quem chama mantém o texto na caixa).
    /// </summary>
    public async Task<ResultadoConta> EnviarFeedbackAsync(string? texto, bool comNome, string versao)
    {
        var corpo = FeedbackDaMumu.MontarCorpo(texto, comNome, versao);
        if (corpo == null) return new(false, "texto_vazio", "Escreva sua mensagem antes de mandar.");
        bool tinhaConta = TemConta;
        ChamadaResultado? r = null;
        for (int tentativa = 0; tentativa < 2; tentativa++)
        {
            var token = await AcessoAsync(forcar: tentativa == 1);
            if (token == null) return new(false, "sem_sessao", TemConta ? TextoSemRede : tinhaConta ? FraseSessaoEncerrada : "Entre na sua conta para mandar sua mensagem.");
            r = await ChamarAsync(HttpMethod.Post, "/feedback", corpo, token);
            if (r.SemRede) return new(false, "sem_rede", TextoSemRede);
            if (r.Status != HttpStatusCode.Unauthorized) break;
        }
        if (r!.Status == HttpStatusCode.OK && r.Json?["ok"]?.GetValue<bool>() == true) return new(true, null, FeedbackDaMumu.Enviado);
        var erro = r.Json?["erro"]?.GetValue<string>();
        return new(false, erro ?? "erro", FeedbackDaMumu.Frase(erro, r.Json?["texto"]?.GetValue<string>()));
    }
}
