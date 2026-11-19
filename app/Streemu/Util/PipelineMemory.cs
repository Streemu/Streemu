namespace Streemu.Util;

/// <summary>
/// Lembra qual pipeline de encoder funcionou da última vez para cada GPU e tamanho de fonte. Na AMD o app testava NVENC,
/// AMF na GPU, Media Foundation (8 s de espera) e QSV antes de chegar ao único que funciona, ~10 s em toda transmissão.
/// O valor lembrado só é tentado PRIMEIRO; se falhar, a busca completa roda como sempre.
/// Uso: <c>memory.Remember(key, "h264_amf|Cpu"); memory.Recall(key);</c>
/// </summary>
public sealed class PipelineMemory
{
    readonly AppState _state;

    public PipelineMemory(AppState state) => _state = state;

    public string? Recall(string key) => _state.PipelineMemory.GetValueOrDefault(key);

    public void Remember(string key, string value)
    {
        if (_state.PipelineMemory.TryGetValue(key, out var old) && old == value) return;
        _state.PipelineMemory[key] = value;
        _state.Save();
    }
}
