using System;

interface ISpiceAPI: IDisposable
{
    public string SpiceHost { get; }
    public bool Connected { get; }
    public int Latency { get; }
    public void GuardConnection();
    public void SendButtonsState(ReadOnlySpan<int> state);
    public void SendAnalogsState(float left, float right);
}
