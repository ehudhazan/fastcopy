using Terminal.Gui;
using FastCopy.Core;

namespace FastCopy.UI;

/// <summary>
/// Interactive TUI menu for configuring FastCopy operations when no CLI arguments are provided.
/// </summary>
public sealed class InteractiveMenu : Dialog
{
    private readonly TextField _sourceField;
    private readonly TextField _destinationField;
    private readonly CheckBox _verifyCheck;
    private readonly CheckBox _dryRunCheck;
    private readonly CheckBox _deleteCheck;
    private readonly TextField _speedLimitField;
    private readonly Label _speedLimitLabel;
    private readonly TextField _onCompleteField;
    private readonly TextField _retriesField;
    private int _selectedSpeedLimitMB = 0;
    
    private bool _confirmed;
    
    public string Source => _sourceField.Text?.ToString() ?? string.Empty;
    public string Destination => _destinationField.Text?.ToString() ?? string.Empty;
    public bool Verify => _verifyCheck.CheckedState == CheckState.Checked;
    public bool DryRun => _dryRunCheck.CheckedState == CheckState.Checked;
    public bool Delete => _deleteCheck.CheckedState == CheckState.Checked;
    public long SpeedLimitBytesPerSec { get; private set; }
    public string OnComplete => _onCompleteField.Text?.ToString() ?? string.Empty;
    public int Retries => int.TryParse(_retriesField.Text?.ToString(), out int r) ? r : 2;
    public bool Confirmed => _confirmed;

    public InteractiveMenu() : base()
    {
        Title = "FastCopy - Interactive Configuration";
        Width = Dim.Percent(80);
        Height = Dim.Percent(80);
        
        var contentHeight = 0;
        
        // Source Path
        var sourceLabel = new Label { X = 1, Y = contentHeight++, Text = "Source Path:" };
        _sourceField = new TextField
        {
            X = 1,
            Y = contentHeight++,
            Width = Dim.Fill(1),
            Text = ""
        };
        
        contentHeight++;
        
        // Destination Path
        var destLabel = new Label { X = 1, Y = contentHeight++, Text = "Destination Path:" };
        _destinationField = new TextField
        {
            X = 1,
            Y = contentHeight++,
            Width = Dim.Fill(1),
            Text = ""
        };
        
        contentHeight++;
        
        // Checkboxes
        _verifyCheck = new CheckBox
        {
            X = 1,
            Y = contentHeight++,
            Text = "Verify (Compare checksums after copy)"
        };
        
        _dryRunCheck = new CheckBox
        {
            X = 1,
            Y = contentHeight++,
            Text = "Dry Run (Simulate without copying)"
        };
        
        _deleteCheck = new CheckBox
        {
            X = 1,
            Y = contentHeight++,
            Text = "Delete (Remove source after successful copy)"
        };
        
        contentHeight++;
        
        // Speed Limit Slider
        var speedLabel = new Label { X = 1, Y = contentHeight++, Text = "Speed Limit (MB/s, 0 = Unlimited):" };
        _speedLimitLabel = new Label 
        { 
            X = 1, 
            Y = contentHeight++, 
            Text = "0" 
        };
        
        // Use a simple TextField instead of Slider for better AOT compatibility
        _speedLimitField = new TextField
        {
            X = 1,
            Y = Pos.Top(_speedLimitLabel),
            Width = 20,
            Text = "0"
        };
        
        _speedLimitField.TextChanged += (s, e) =>
        {
            try
            {
                var text = _speedLimitField.Text?.ToString() ?? "0";
                if (int.TryParse(text, out int mbValue) && mbValue >= 0)
                {
                    _selectedSpeedLimitMB = mbValue;
                    if (mbValue == 0)
                    {
                        SpeedLimitBytesPerSec = 0;
                        _speedLimitLabel.Text = "Unlimited";
                    }
                    else
                    {
                        SpeedLimitBytesPerSec = mbValue * 1024L * 1024L;
                        _speedLimitLabel.Text = $"{mbValue} MB/s";
                    }
                }
            }
            catch
            {
                // Ignore parse errors
            }
        };
        
        contentHeight++;
        
        // On-Complete Command
        var onCompleteLabel = new Label { X = 1, Y = contentHeight++, Text = "On Complete Command (optional):" };
        _onCompleteField = new TextField
        {
            X = 1,
            Y = contentHeight++,
            Width = Dim.Fill(1),
            Text = ""
        };
        
        contentHeight++;
        
        // Retry Count
        var retriesLabel = new Label { X = 1, Y = contentHeight++, Text = "Max Retries:" };
        _retriesField = new TextField
        {
            X = 1,
            Y = contentHeight++,
            Width = 10,
            Text = "2"
        };
        
        contentHeight += 2;
        
        // Buttons
        var startButton = new Button
        {
            X = Pos.Center() - 15,
            Y = contentHeight,
            Text = "Start Copy",
            IsDefault = true
        };
        
        startButton.Accepting += (s, e) =>
        {
            if (ValidateInputs())
            {
                _confirmed = true;
                Application.RequestStop();
            }
        };
        
        var cancelButton = new Button
        {
            X = Pos.Center() + 5,
            Y = contentHeight,
            Text = "Cancel"
        };
        
        cancelButton.Accepting += (s, e) =>
        {
            _confirmed = false;
            Application.RequestStop();
        };
        
        // Add all views
        Add(sourceLabel, _sourceField);
        Add(destLabel, _destinationField);
        Add(_verifyCheck, _dryRunCheck, _deleteCheck);
        Add(speedLabel, _speedLimitLabel, _speedLimitField);
        Add(onCompleteLabel, _onCompleteField);
        Add(retriesLabel, _retriesField);
        Add(startButton, cancelButton);
    }

    private bool ValidateInputs()
    {
        var source = _sourceField.Text?.ToString() ?? string.Empty;
        var dest = _destinationField.Text?.ToString() ?? string.Empty;
        
        if (string.IsNullOrWhiteSpace(source))
        {
            MessageBox.ErrorQuery("Validation Error", "Source path cannot be empty.", "OK");
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(dest))
        {
            MessageBox.ErrorQuery("Validation Error", "Destination path cannot be empty.", "OK");
            return false;
        }
        
        // Validate retries field
        if (!int.TryParse(_retriesField.Text?.ToString(), out int retries) || retries < 0)
        {
            MessageBox.ErrorQuery("Validation Error", "Max Retries must be a non-negative integer.", "OK");
            return false;
        }
        
        return true;
    }

    /// <summary>
    /// Displays the interactive menu and returns the configuration settings.
    /// Returns null if the user cancels.
    /// </summary>
    public static InteractiveMenu? Run()
    {
        var menu = new InteractiveMenu();
        Application.Run(menu);
        
        return menu.Confirmed ? menu : null;
    }
}
