using Godot;
using System;
using System.Net;

public partial class Options : Node
{
    private Config _config;
    private LineEdit _spiceApiHostEdit;
    private Label _spiceApiHostLabel;
    private LineEdit _spiceApiPortEdit;
    private Label _spiceApiPortLabel;
    private CheckButton _spiceApiUseUdp;
    private Slider _faderAreaSlider;
    private Label _faderAreaLabel;

    private Button _okButton;

    public override void _Ready()
    {
        _spiceApiHostEdit = GetNode<LineEdit>("Control/Container/VBoxContainer/SpiceApiHost");
        _spiceApiHostLabel = GetNode<Label>("Control/Container/VBoxContainer/SpiceApiHostLabel");
        _spiceApiPortEdit = GetNode<LineEdit>("Control/Container/VBoxContainer/SpiceApiPort");
        _spiceApiPortLabel = GetNode<Label>("Control/Container/VBoxContainer/SpiceApiPortLabel");
        _spiceApiUseUdp = GetNode<CheckButton>("Control/Container/VBoxContainer/SpiceApiUseUdp");

        _faderAreaSlider = GetNode<Slider>("Control/Container/VBoxContainer/FaderAreaSlider");
        _faderAreaLabel = GetNode<Label>("Control/Container/VBoxContainer/FaderAreaLabel");

        _faderAreaSlider.ValueChanged += FaderAreaSlider_ValueChanged;

        _okButton = GetNode<Button>("Control/Container/OKButton");
        _okButton.Pressed += OkButton_Pressed;

        _config = Config.EnsureInited();
        _spiceApiHostEdit.Text = _config.SpiceApiHost;
        _spiceApiPortEdit.Text = _config.SpiceApiPort.ToString();
        _spiceApiUseUdp.ButtonPressed = _config.UseUdp;
        _faderAreaSlider.Value = _config.FaderAreaSize * 100;
        FaderAreaSlider_ValueChanged(_faderAreaSlider.Value);
    }

    private bool ValidateAndSaveOptions()
    {
        _spiceApiHostLabel.RemoveThemeColorOverride("font_color");
        _spiceApiPortLabel.RemoveThemeColorOverride("font_color");  

        bool ok = true;
        if (!string.IsNullOrEmpty(_spiceApiHostEdit.Text) &&
            !IPAddress.TryParse(_spiceApiHostEdit.Text, out _))
        {
            _spiceApiHostLabel.AddThemeColorOverride("font_color", Colors.Red);
            _spiceApiHostEdit.GrabFocus();
            ok = false;
        }

        ushort portParsed = 0;
        if (!string.IsNullOrEmpty(_spiceApiHostEdit.Text) &&
            !ushort.TryParse(_spiceApiPortEdit.Text, out portParsed))
        {
            _spiceApiPortLabel.AddThemeColorOverride("font_color", Colors.Red);
            _spiceApiPortEdit.GrabFocus();
            ok = false;
        }

        if (ok)
        {
            _config.SpiceApiHost = _spiceApiHostEdit.Text;
            _config.SpiceApiPort = portParsed;
            _config.UseUdp = _spiceApiUseUdp.ButtonPressed;
            _config.FaderAreaSize = (float)(_faderAreaSlider.Value / 100);
            _config.Save();
        }

        return ok;
    }
    private void FaderAreaSlider_ValueChanged(double value)
    {
        GD.Print("changed");
        _faderAreaLabel.Text = $"{value:00.0}%";
    }

    private void OkButton_Pressed()
    {
        GD.Print("pressed");
        if (ValidateAndSaveOptions())
        {
            GetTree().ChangeSceneToFile("res://Main.tscn");
        }
    }
}
