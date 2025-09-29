using Godot;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Sockets.Kcp;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class UdpSpiceAPI : ISpiceAPI, IKcpCallback
{
    private UdpClient _client;
    private IPEndPoint _selfEp;
    private CancellationTokenSource _stopThread = new();

    private readonly IPEndPoint _targetEp;
    private readonly ConcurrentQueue<Action> _sendTasks = new();
    private readonly Thread _thread;

    private readonly SimpleSegManager.Kcp _kcp;

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

        _client = new UdpClient(0);

        _kcp = new(573, this);
        // fast mode
        _kcp.NoDelay(1, 4, 2, 1);
        _kcp.WndSize();

        _thread = new Thread(UpdateThread);
        _thread.Start();

        BeginRecv();
    }

    private void ProcessKcpRecv()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var recvSize = _kcp.Recv(buffer);
            if (recvSize > 0)
            {
                GD.Print($"KCP received: {Encoding.UTF8.GetString(buffer.AsSpan(0, recvSize))}");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    void UpdateThread()
    {
        var recvBuffer = new byte[1024 * 64];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!_stopThread.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (_kcp.Check(now) >= now)
                _kcp.Update(now);

            ProcessKcpRecv();

            while (true)
            {
                if (!_sendTasks.TryDequeue(out var task))
                    break;

                task();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Send(string data)
    {
        void SendAction()
        {
            if (_kcp is null)
                return;

            var byteLen = Encoding.UTF8.GetByteCount(data);
            Span<byte> bytes = stackalloc byte[byteLen + 1];
            Encoding.ASCII.GetBytes(data, bytes);
            bytes[^1] = 0;

            for (var result = -2; result == -2;)
            {
                result = _kcp.Send(bytes);
                Thread.Sleep(0);
            }
        }

        _sendTasks.Enqueue(SendAction);
    }

    public void GuardConnection()
    {
        // Send empty packet to keep kcp connection alive
        // if connected
        Send("");
    }

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
        _thread.Join();

        _sendTasks.Clear();
        _stopThread.Dispose();
        _stopThread = null;

        _kcp.Dispose();
        _client.Dispose();
    }

    public void Output(IMemoryOwner<byte> buffer, int len)
    {
        try
        {
            _client.Send(buffer.Memory.Span.Slice(0, len), _targetEp);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"failed to send kcp output message to {_targetEp} ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private async void BeginRecv()
    {
        if (_stopThread == null || _stopThread.IsCancellationRequested)
            return;

        try
        {
            var result = await _client.ReceiveAsync();
            _kcp.Input(result.Buffer);
        }
        catch (Exception ex)
        {
            if (ex is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
                await Task.Delay(0);
            }
            else
            {
                GD.PrintErr($"failed to recv kcp message ({ex.GetType().Name}): {ex.Message}");
            }
        }

        BeginRecv();
    }
}
