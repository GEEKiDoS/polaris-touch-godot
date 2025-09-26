using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

public record Finger
{
    public Finger(Vector2 pos, int idx)
    {
        Position = pos;
        Index = idx;
        MoveTime = PressTime = Stopwatch.GetTimestamp();
        StartPos = pos;
    }

    public Vector2 Position { get; set; }
    public Vector2 StartPos { get; set; }
    public int Index { get; set; }
    public long PressTime { get; set; }
    public long MoveTime { get; set; }
    public bool IsVaild { get; set; }

    public void New(Vector2 pos, int idx)
    {
        Position = pos;
        Index = idx;
        MoveTime = PressTime = Stopwatch.GetTimestamp();
        StartPos = pos;
        IsVaild = true;
    }

    public void Update(Vector2 pos)
    {
        Position = pos;
        MoveTime = Stopwatch.GetTimestamp();
    }

    public void Release()
    {
        IsVaild = false;
        Index = -1;
    }
}

public partial class View : Node
{
    private readonly System.Threading.Lock _fingerLock = new();
    private readonly ConcurrentDictionary<int, int> _fingersMap = [];
    private readonly List<Finger> _fingers = [];
    private List<ColorRect> _buttons;
    private int[] _laneState;
    private Config _config;
    private Window _window;

    private Finger _leftFaderFinger = new(new(), -1);
    private Finger _rightFaderFinger = new(new(), -1);
    private float _leftFaderAnalog = 0.5f;
    private float _rightFaderAnalog = 0.5f;
    private int _leftFaderDir = 0;
    private int _rightFaderDir = 0;
    private TextureRect _leftFader;
    private TextureRect _rightFader;
    private Label _faderPositionLabel;
    private Label _connectionStateLabel;
    private ISpiceAPI _connection;
    private static readonly Color COLOR_TOUCH = new Color(1.0f, 1.0f, 1.0f, 0.3f);
    private static readonly Color COLOR_NORMAL = Color.FromHtml("#000");

    public override void _Ready()
    {
        _config = Config.EnsureInited();

        if (!_config.LoadSuccess)
        {
            // first open
            GetTree().ChangeSceneToFile("res://Options.tscn");
        }

        _window = GetWindow();

        var buttons = GetNode("UI/Lane/Buttons");
        _buttons = [.. buttons.GetChildren()
            .Where(x => x is ColorRect)
            .Select(x => x as ColorRect)];

        _laneState = new int[_buttons.Count];

        _leftFader = GetNode<TextureRect>("UI/Fader/LeftFader");
        _rightFader = GetNode<TextureRect>("UI/Fader/RightFader");

        _faderPositionLabel = GetNode<Label>("UI/Status/HBoxContainer/FaderPosition");
        _connectionStateLabel = GetNode<Label>("UI/Status/HBoxContainer/ConnectionStatus");

        var spiceHostLabel = GetNode<Label>("UI/Status/HBoxContainer/SpiceApiAddress");

        _connection = _config.UseUdp ?
            new UdpSpiceAPI(_config.SpiceApiHost, _config.SpiceApiPort) :
            new TcpSpiceAPI(_config.SpiceApiHost, _config.SpiceApiPort);

        spiceHostLabel.Text = _connection.SpiceHost;

        var faderArea = GetNode<Control>("UI/Fader");
        var laneArea = GetNode<Control>("UI/Lane");

        faderArea.AnchorBottom = _config.FaderAreaSize;
        laneArea.AnchorTop = _config.FaderAreaSize;

        _guard = GetNode<Timer>("ConnectionGuard");
        _guard.Timeout += Guard_Timeout;
        _guard.Start();

        if (_config.DebugTouch)
        {
            _itemList = GetNode<ItemList>("UI/ItemList");
            _itemList.Visible = true;
        }
    }

    private void Guard_Timeout()
    {
        _connection.GuardConnection();
    }

    public override void _Input(InputEvent e)
    {
        lock (_fingerLock)
        {
            if (e is InputEventScreenTouch touch)
            {
                if (touch.Pressed)
                {
                    CreateFinger(touch.Position, touch.Index);
                }
                else
                {
                    int idx = _fingersMap[touch.Index];
                    Finger finger = _fingers[idx];
                    finger.Release();
                    _fingersMap.Remove(touch.Index, out _);
                }
                _window.SetInputAsHandled();
            }
            else if (e is InputEventScreenDrag drag)
            {
                if (_fingersMap.TryGetValue(drag.Index, out var idx))
                {
                    Finger finger = _fingers[idx];
                    finger.Update(drag.Position);
                }
                else
                {
                    CreateFinger(drag.Position, drag.Index);
                }
                _window.SetInputAsHandled();
            }
        }
    }

    private void CreateFinger(Vector2 position, int index)
    {
        lock (_fingerLock)
        {
            Finger? finger = null;
            int idx = -1;
            var span = CollectionsMarshal.AsSpan(_fingers);
            for (var i = 0; i < span.Length; i++)
            {
                var item = span[i];
                if (!item.IsVaild)
                {
                    finger = item;
                    idx = i;
                }
            }
            if (finger == null)
            {
                finger = new(position, index)
                {
                    IsVaild = true
                };
                _fingers.Add(finger);
                idx = _fingers.Count - 1;
            }
            else
            {
                finger.New(position, index);
            }
            _fingersMap[index] = idx;
        }
    }

    private static (float, bool) UpdateFaderAnalog(int dir, float analog, float frameTime)
    {
        if (dir == 0)
        {
            if (analog == 0.5f)
                return (analog, false);

            analog += (0.5f - analog) * 1.92f;

            if (Mathf.Abs(0.5f - analog) < 0.001f)
            {
                analog = 0.5f;
            }
        }
        else
        {
            var dest = dir > 0 ? 1 : 0;
            if (analog == dest)
            {
                return (analog, false);
            }

            analog += (dest - analog) / 6;

            if (Mathf.Abs(dest - analog) < 0.001f)
            {
                analog = dest;
            }
        }

        return (analog, true);
    }

    private static readonly TimeSpan FIND_OPPOSITE_FADER_DELAY = TimeSpan.FromSeconds(0.5);

    private static Func<Finger, bool> FilterNewFaderFinger(Finger? another, int halfWidth, int halfHeight, Func<float, float, bool> cmpFunc)
    {
        return v =>
        {
            if (v.StartPos.Y > halfHeight)
                return false;

            if (!another.IsVaild)
            {
                if (!cmpFunc(v.StartPos.X, halfWidth))
                    return false;

                return true;
            }

            if (v.Index == another.Index)
                return false;

            if (!cmpFunc(v.StartPos.X, another.Position.X))
                return false;

            if ((v.PressTime - another.PressTime) / (double)Stopwatch.Frequency < FIND_OPPOSITE_FADER_DELAY.TotalSeconds && !cmpFunc(v.StartPos.X, halfWidth))
                return false;

            return true;
        };
    }

    private void UpdateFaderState(List<Finger> fingers, float frameTime)
    {
        var halfHeight = (int)(_window.Size.Y * _config.FaderAreaSize);
        var halfWidth = _window.Size.X / 2;

        if (!_leftFaderFinger.IsVaild)
        {
            var finger = fingers.FirstOrDefault(FilterNewFaderFinger(_rightFaderFinger, halfWidth, halfHeight, (a, b) => a < b));
            if (finger != null)
            {
                _leftFaderFinger.New(finger.Position, finger.Index);
            }
        }
        else
        {
            var newState = fingers.FirstOrDefault(v => v.Index == _leftFaderFinger.Index);

            if (newState != null)
            {
                var delta = newState.Position.X - _leftFaderFinger.Position.X;
                if (MathF.Abs(delta) > _config.FaderDeadZone)
                    _leftFaderDir = Math.Sign(delta);
                _leftFaderFinger.New(newState.Position, newState.Index);
            }
            else
            {
                _leftFaderDir = 0;
                _leftFaderFinger.Release();
            }
        }

        if (!_rightFaderFinger.IsVaild)
        {
            var finger = fingers.FirstOrDefault(FilterNewFaderFinger(_leftFaderFinger, halfWidth, halfHeight, (a, b) => a > b));
            if (finger != null)
            {
                _rightFaderFinger.New(finger.Position, finger.Index);
            }
        }
        else
        {
            var newState = fingers.FirstOrDefault(v => v.Index == _rightFaderFinger.Index);
            if (newState != null)
            {
                var delta = newState.Position.X - _rightFaderFinger.Position.X;
                if (MathF.Abs(delta) > _config.FaderDeadZone)
                    _rightFaderDir = Math.Sign(delta);
                _rightFaderFinger.New(newState.Position, newState.Index);
            }
            else
            {
                _rightFaderDir = 0;
                _rightFaderFinger.Release();
            }
        }

        var leftUpdated = false;
        (_leftFaderAnalog, leftUpdated) = UpdateFaderAnalog(_leftFaderDir, _leftFaderAnalog, frameTime);

        var rightUpdated = false;
        (_rightFaderAnalog, rightUpdated) = UpdateFaderAnalog(_rightFaderDir, _rightFaderAnalog, frameTime);

        if (leftUpdated || rightUpdated)
        {
            _connection.SendAnalogsState(_leftFaderAnalog, _rightFaderAnalog);
        }
    }

    private void UpdateLaneState(List<Finger> fingers)
    {
        Array.Fill(_laneState, 0);

        if (fingers.Count != 0)
        {
            var halfHeight = _window.Size.Y * _config.FaderAreaSize;
            var laneWidth = _window.Size.X / (float)_laneState.Length;

            foreach (var finger in fingers)
            {
                var x = finger.Position.X;

                if (finger.StartPos.Y < halfHeight)
                    continue;

                var lane = (int)(x / laneWidth);
                _laneState[lane]++;
            }
        }

        _connection.SendButtonsState(_laneState);
    }

    long? optionStartHoldTime = null;
    private Timer _guard;
    private ItemList _itemList;

    private void DetectOptionHold(List<Finger> fingers)
    {
        var isOptionHold = fingers.Any(f => f.Position.X < 256 && f.Position.Y < 256);
        if (isOptionHold)
        {
            if (optionStartHoldTime is null)
            {
                optionStartHoldTime = Stopwatch.GetTimestamp();
                return;
            }

            var duration = Stopwatch.GetTimestamp() - optionStartHoldTime.Value;

            if (duration / (double)Stopwatch.Frequency >= 1)
            {
                GetTree().ChangeSceneToFile("res://Options.tscn");
            }

            return;
        }

        optionStartHoldTime = null;
    }

    private static readonly TimeSpan INVALID_FINGER_DELAY = TimeSpan.FromSeconds(2);

    public override void _PhysicsProcess(double frameTime)
    {
        List<Finger> fingers = _fingers;

        lock (_fingerLock)
        {
            fingers = _fingers.Where(v => v.IsVaild).ToList();
        }

        if (_config.DebugTouch)
        {
            _itemList.Clear();
            foreach (var finger in fingers)
            {
                _itemList.AddItem(finger.ToString());
            }
        }
        UpdateFaderState(fingers, (float)frameTime);
        UpdateLaneState(fingers);
        DetectOptionHold(fingers);

    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            var state = _laneState[i];
            var button = _buttons[i];

            if (state > 0)
                button.Color = COLOR_TOUCH;
            else
                button.Color = COLOR_NORMAL;
        }

        _leftFader.AnchorLeft = _leftFader.AnchorRight = _leftFaderAnalog * 0.5f;
        _rightFader.AnchorLeft = _rightFader.AnchorRight = 0.5f + _rightFaderAnalog * 0.5f;

        _faderPositionLabel.Text = $"{_leftFaderAnalog:F2}, {_rightFaderAnalog:F2}";
        _connectionStateLabel.Text = _config.UseUdp ? "UDP MODE" :
            _connection.Connected ? "CONNECTED" : "DISCONNECTED";
    }

    public override void _ExitTree()
    {
        _guard.Stop();
        _connection.Dispose();
        _connection = null;
    }
}
