using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
class UdpSpiceAPI : ISpiceAPI
{
    private UdpClient _client;
    private readonly IPEndPoint _targetEp;

    private readonly ConcurrentQueue<Action> _sendTasks = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stopThread = new();
    private readonly ManualResetEvent _queueEvent = new(false);

    private bool _disposed = false;
    private volatile bool _trying;

    public bool Connected => _client != null;
    public string SpiceHost { get; private set; }

    public UdpSpiceAPI(string host, ushort port)
    {
        var parsed = host.Split('.').Select(byte.Parse).ToArray();
        var ip = new IPAddress(parsed);

        GD.Print($"parsed ip: {ip}");

        _targetEp = new IPEndPoint(ip, port);
        _trying = false;

        SpiceHost = $"{_targetEp}";

        _client = new UdpClient();

        _thread = new Thread(SendThread);
        _thread.Start();
    }

    void SendThread()
    {
        while (!_stopThread.IsCancellationRequested)
        {
            _queueEvent.WaitOne();

            while (true)
            {
                if (!_sendTasks.TryDequeue(out var task))
                    break;

                // Don't wait for connect, just discard
                task();
            }

            _queueEvent.Reset();
        }

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Send(string data)
    {
        void SendAction()
        {
            if (_client is null)
                return;

            Span<byte> bytes = stackalloc byte[data.Length + 1];
            Encoding.ASCII.GetBytes(data, bytes);
            bytes[^1] = 0;

            try
            {
                _client.Client.SendTo(bytes, _targetEp);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"failed to send to spice ({ex.GetType().Name}): {ex.Message}");
            }

        }

        _sendTasks.Enqueue(SendAction);
        _queueEvent.Set();
    }

    public void GuardConnection() { }

    int _lastId = 0;

    bool[] _lastButtonState = new bool[12];
    string[] packetParamsBuffer = new string[12];

    public void SendButtonsState(ReadOnlySpan<int> state, bool delta = true)
    {
        if (state.Length > 12)
        {
            GD.PrintErr("state count is bigger than 12, 何意味");
            return;
        }

        int paramCount = 0;
        for (int i = 0; i < state.Length; i++)
        {
            var newState = state[i] > 0;
            if (delta && newState == _lastButtonState[i])
                continue;

            _lastButtonState[i] = newState;
            packetParamsBuffer[paramCount++] = $"[\"Button {i + 1}\",{(newState ? "1" : "0")}]";
        }

        if (paramCount == 0)
            return;

        var paramStr = string.Join(',', packetParamsBuffer.Take(paramCount));
        Send($"{{\"id\":{_lastId++},\"module\":\"buttons\",\"function\":\"write\",\"params\":[{paramStr}]}}");
    }

    float _lastLeftFader = -1;
    float _lastRightFader = -1;

    public void SendAnalogsState(float left, float right, bool delta = true)
    {
        int paramCount = 0;
        if (!delta || left != _lastLeftFader)
        {
            _lastLeftFader = left;
            packetParamsBuffer[paramCount++] = $"[\"Fader-L\",{left:F2}]";
        }

        if (!delta || right != _lastRightFader)
        {
            _lastRightFader = right;
            packetParamsBuffer[paramCount++] = $"[\"Fader-R\",{right:F2}]";
        }

        if (paramCount == 0)
            return;

        var paramStr = string.Join(',', packetParamsBuffer.Take(paramCount));
        Send($"{{\"id\":{_lastId++},\"module\":\"analogs\",\"function\":\"write\",\"params\":[{paramStr}]}}");
    }

    public void Dispose()
    {
        if (_disposed) return;

        _stopThread.Cancel();
        _queueEvent.Set();
        _thread.Join();

        _queueEvent.Dispose();
        _stopThread.Dispose();

        if (_client != null)
        {
            _client.Dispose();
        }
    }
}
