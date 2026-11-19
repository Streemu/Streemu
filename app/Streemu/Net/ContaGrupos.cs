using System.Net;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>Resposta de uma operação de grupo. <see cref="Texto"/> é em português, diz a próxima ação e nunca leva token nem convite de OUTRO grupo.</summary>
public sealed record ResultadoGrupo(bool Ok, string? Erro = null, string? Texto = null, string? Id = null, string? Nome = null, string? Convite = null, bool Acabou = false);

public sealed record MembroInfo(string Id, string Apelido, string Papel);

/// <summary>O que o grupo diz de si para um membro (<c>GET /grupos/&lt;id&gt;</c>). <see cref="Convite"/> vem preenchido para todo membro.</summary>
public sealed record GrupoInfo(string Id, string Nome, string Papel, string? Convite, IReadOnlyList<MembroInfo> Membros);

/// <summary>
/// Operações de grupo da conta (spec 019, etapa 8): criar, entrar por convite, sair, ver, renomear, trocar o convite e remover membro.
/// Todas falam com o Worker com o token de acesso e devolvem uma frase pronta para a tela (motivo provável + próxima ação).
/// Depois de criar ou entrar, o servidor passa a saber do grupo e o token ganha o campo <c>g</c> só ao renovar: por isso a lista local é
/// refeita e o token renovado já aqui (senão o primeiro grupo da pessoa esperaria até 1 h).
/// </summary>
public sealed partial class Conta
{
    /// <summary>Código do servidor (<c>erro</c>) → frase para a pessoa. O texto do servidor serve de reserva.</summary>
    public static string FraseDeGrupo(string? erro, string? textoServidor = null) => erro switch
    {
        "convite_invalido" => "Esse convite não vale mais. Peça um novo a quem te convidou.",
        "grupo_cheio" => "Esse grupo já está cheio. Peça ao dono para abrir espaço.",
        "muitos_grupos" => "Você já está no máximo de grupos. Saia de um para entrar em outro.",
        "muitas_tentativas" => "Muitos convites errados em pouco tempo. Espere uns 10 minutos e tente de novo.",
        "nao_membro" => "Você não está mais neste grupo. A lista vai ser atualizada.",
        "so_dono" => "Só quem criou o grupo faz isso.",
        "token_recusado" or "conta_invalida" => FraseSessaoEncerrada,
        "requisicao_invalida" => string.IsNullOrWhiteSpace(textoServidor) ? "O servidor não entendeu o pedido. Confira o que digitou e tente de novo." : textoServidor,
        "servidor_ocupado" => "O servidor está ocupado agora. Tente de novo em instantes.",
        "nao_encontrado" => "Esse grupo não existe mais. A lista vai ser atualizada.",
        _ => string.IsNullOrWhiteSpace(textoServidor) ? "Não deu certo. Tente de novo em instantes." : textoServidor,
    };

    /// <summary>Chama uma rota de grupo com o token (renova e repete uma vez num 401). Nulo + texto = sem conta, sessão encerrada ou sem rede.</summary>
    async Task<(ChamadaResultado? R, string? Texto)> GrupoChamarAsync(HttpMethod metodo, string caminho, JsonObject? corpo)
    {
        bool tinhaConta = TemConta;
        ChamadaResultado? r = null;
        for (int tentativa = 0; tentativa < 2; tentativa++)
        {
            var token = await AcessoAsync(forcar: tentativa == 1);
            if (token == null) return (null, TemConta ? TextoSemRede : tinhaConta ? FraseSessaoEncerrada : "Entre na sua conta primeiro.");
            r = await ChamarAsync(metodo, caminho, corpo, token);
            if (r.SemRede) return (null, TextoSemRede);
            if (r.Status != HttpStatusCode.Unauthorized) break;
        }
        if (r!.Json?["erro"]?.GetValue<string>() == "conta_invalida") { Apagar(); return (null, FraseSessaoEncerrada); }
        return (r, null);
    }

    ResultadoGrupo Falha(ChamadaResultado r)
    {
        var erro = r.Json?["erro"]?.GetValue<string>() ?? "erro";
        return new(false, erro, FraseDeGrupo(erro, r.Json?["texto"]?.GetValue<string>()));
    }

    /// <summary>Depois de criar ou entrar: renova o token (o campo <c>g</c>) e a lista local de grupos.</summary>
    async Task AtualizarGruposAsync()
    {
        await AcessoAsync(forcar: true); // o /conta/renovar devolve a lista de grupos e GuardarSessao a guarda
        _salvar();
        Changed?.Invoke();
    }

    public async Task<ResultadoGrupo> CriarGrupoAsync(string nome, string apelido)
    {
        var (r, texto) = await GrupoChamarAsync(HttpMethod.Post, "/grupos", new JsonObject { ["nome"] = nome.Trim(), ["apelido"] = apelido });
        if (r == null) return new(false, "sem_conta", texto);
        if (r.Status != HttpStatusCode.OK || r.Json?["id"]?.GetValue<string>() is not string id) return Falha(r);
        await AtualizarGruposAsync();
        return new(true, null, null, id, r.Json["nome"]?.GetValue<string>() ?? nome, r.Json["convite"]?.GetValue<string>());
    }

    /// <summary>Entra por convite (o código, com ou sem hífens, ou o link <c>https://servidor/g/&lt;código&gt;</c> já reduzido ao código).</summary>
    public async Task<ResultadoGrupo> EntrarAsync(string convite, string apelido)
    {
        var (r, texto) = await GrupoChamarAsync(HttpMethod.Post, "/grupos/entrar", new JsonObject { ["convite"] = convite, ["apelido"] = apelido });
        if (r == null) return new(false, "sem_conta", texto);
        if (r.Status != HttpStatusCode.OK || r.Json?["id"]?.GetValue<string>() is not string id) return Falha(r);
        await AtualizarGruposAsync();
        return new(true, null, null, id, r.Json["nome"]?.GetValue<string>());
    }

    public async Task<ResultadoGrupo> SairDoGrupoAsync(string grupoId)
    {
        var (r, texto) = await GrupoChamarAsync(HttpMethod.Post, "/grupos/" + Uri.EscapeDataString(grupoId) + "/sair", new JsonObject());
        if (r == null) return new(false, "sem_conta", texto);
        if (r.Status != HttpStatusCode.OK) return Falha(r);
        await AtualizarGruposAsync();
        return new(true, Acabou: r.Json?["acabou"]?.GetValue<bool>() == true);
    }

    public async Task<(GrupoInfo? Info, string? Texto)> InfoAsync(string grupoId)
    {
        var (r, texto) = await GrupoChamarAsync(HttpMethod.Get, "/grupos/" + Uri.EscapeDataString(grupoId), null);
        if (r == null) return (null, texto);
        if (r.Status != HttpStatusCode.OK || r.Json == null) { var f = Falha(r); if (f.Erro is "nao_membro" or "nao_encontrado") await AtualizarGruposAsync(); return (null, f.Texto); }
        var membros = (r.Json["membros"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(m => new MembroInfo(m["id"]?.GetValue<string>() ?? "", m["apelido"]?.GetValue<string>() ?? "", m["papel"]?.GetValue<string>() ?? "membro")).ToList();
        return (new GrupoInfo(r.Json["id"]?.GetValue<string>() ?? grupoId, r.Json["nome"]?.GetValue<string>() ?? "", r.Json["papel"]?.GetValue<string>() ?? "membro",
            r.Json["convite"]?.GetValue<string>(), membros), null);
    }

    public async Task<ResultadoGrupo> RenomearAsync(string grupoId, string nome)
    {
        var (r, texto) = await GrupoChamarAsync(HttpMethod.Post, "/grupos/" + Uri.EscapeDataString(grupoId) + "/nome", new JsonObject { ["nome"] = nome.Trim() });
        if (r == null) return new(false, "sem_conta", texto);
        if (r.Status != HttpStatusCode.OK) return Falha(r);
        var novo = r.Json?["nome"]?.GetValue<string>() ?? nome.Trim();
        var g = _estado.Grupos.FirstOrDefault(x => x.Id == grupoId);
        if (g != null) { g.Nome = novo; _salvar(); Changed?.Invoke(); }
        return new(true, Id: grupoId, Nome: novo);
    }

    /// <summary>Gera um convite novo; o antigo deixa de valer na hora. Só o dono.</summary>
    public async Task<ResultadoGrupo> TrocarConviteAsync(string grupoId)
    {
        var (r, texto) = await GrupoChamarAsync(HttpMethod.Post, "/grupos/" + Uri.EscapeDataString(grupoId) + "/convite", new JsonObject());
        if (r == null) return new(false, "sem_conta", texto);
        if (r.Status != HttpStatusCode.OK || r.Json?["convite"]?.GetValue<string>() is not string c) return Falha(r);
        return new(true, Id: grupoId, Convite: c);
    }

    /// <summary>O dono tira um membro do grupo.</summary>
    public async Task<ResultadoGrupo> RemoverAsync(string grupoId, string contaDoMembro)
    {
        var (r, texto) = await GrupoChamarAsync(HttpMethod.Post, "/grupos/" + Uri.EscapeDataString(grupoId) + "/remover", new JsonObject { ["conta"] = contaDoMembro });
        if (r == null) return new(false, "sem_conta", texto);
        if (r.Status != HttpStatusCode.OK) return Falha(r);
        return new(true, Id: grupoId);
    }

    /// <summary>O link para mandar a um amigo: abre a página do convite no servidor do grupo.</summary>
    /// <summary>Link de convite: no site (www.streemu.com.br/g/...) quando o servidor diz qual é (<c>site</c> no /conta/config); senão, no próprio servidor do grupo.</summary>
    public string LinkDoConvite(string convite) => $"{_site ?? _servidor}/g/{convite.Replace("-", "").ToUpperInvariant()}";
}
