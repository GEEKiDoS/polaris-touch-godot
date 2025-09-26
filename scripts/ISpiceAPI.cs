using System;

interface ISpiceAPI: IDisposable
{
    public string SpiceHost { get; }
    public bool Connected { get; }
    public void GuardConnection();
    public void SendButtonsState(ReadOnlySpan<int> state, bool delta = true);
    public void SendAnalogsState(float left, float right, bool delta = true);
}
