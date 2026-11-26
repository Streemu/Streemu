using System.Text.Json.Nodes;
using Streemu.Telemetria;

static class TelemetryCheck
{
    // Este teste só pode falar com o Worker LOCAL; nunca lê grupo.json ou o token real do usuário.
    public static async Task<int> Run()
    {
        string dir = Path.GetFullPath(Path.Combine("_local", "sincronia", "telemetria"));
        Directory.CreateDirectory(dir); Environment.SetEnvironmentVariable("STREEMU_TEST_DIR", dir);
        string id = "sincronia-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var session = SessaoLocal.Criar(id, null, naoEnviar: false);
        session.Evento("sessao_inicio", new() { ["versao"] = "teste-sincronia", ["canal"] = "dev" });
        using (var series = new SerieSincronia(session))
            for (int i = 0; i < 5; i++) series.Add("live-artificial", "ligacao-artificial", "assiste", new() { ["audio_menos_video_ms"] = i, ["medida"] = "relogios_do_player" });
        session.E.Tetos[session.ArquivoSincronia] = long.MaxValue; // a série só sobe liberada por um disparo (A6); aqui o que se confere é o envio, não o disparo
        using var send = new Envio("http://127.0.0.1:8787", session, () => new Identidade(null, null, "teste-sincronia", "teste-sincronia-local")) { Token = _ => Environment.GetEnvironmentVariable("STREEMU_TESTE_TOKEN_CONTA") }; // sem conta nada sobe (spec 022): passe o acesso de uma conta de teste
        send.Ativo = true; send.Iniciar(Array.Empty<SessaoLocal>()); send.Agora();
        var timeout = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < timeout && send.Situacao().Estado != EstadoEnvio.Ok) await Task.Delay(200);
        Gravador.Descarregar(TimeSpan.FromSeconds(10)); // a gravação é assíncrona (etapa 8)
        string serie = session.ArquivoSincronia; // uma série por live: sincronia-1.ndjson
        long size = new FileInfo(Path.Combine(session.Dir, serie)).Length;
        bool ok = send.Situacao().Estado == EstadoEnvio.Ok && session.Cursor(serie) == size;
        var result = new JsonObject { ["pc"] = send.Pc, ["sessao"] = id, ["bytes"] = size, ["cursor"] = session.Cursor(serie), ["confirmado"] = ok };
        File.WriteAllText(Path.Combine(dir, "resultado.json"), result.ToJsonString());
        send.Fechar(TimeSpan.FromSeconds(2));
        Console.WriteLine(ok ? "OK    servidor local confirmou a série de sincronia inteira" : "FALHA telemetria local: " + send.Situacao().Detalhe);
        return ok ? 0 : 1;
    }
}
