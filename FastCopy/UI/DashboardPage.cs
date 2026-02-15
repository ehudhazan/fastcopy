using Spectre.Console;
using Spectre.Console.Rendering;

namespace FastCopy.UI;

/// <summary>
/// Main dashboard renderer for FastCopy using Spectre.Console.
/// Optimized for NativeAOT with surgical rendering updates.
/// </summary>
public sealed class DashboardRenderer
{
    private readonly DashboardViewModel _viewModel;

    public DashboardRenderer(DashboardViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    /// <summary>
    /// Render the complete dashboard layout.
    /// Called by Spectre.Console's LiveDisplay on each update.
    /// </summary>
    public IRenderable Render()
    {
        // Build layout with Header, Body (workers table), and Footer
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(3),
                new Layout("Body"),
                new Layout("Footer").Size(5)
            );

        layout["Header"].Update(RenderHeader());
        layout["Body"].Update(RenderWorkersTable());
        layout["Footer"].Update(RenderFooter());

        return layout;
    }

    private IRenderable RenderHeader()
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        var leftPanel = new Panel(new Markup("[cyan bold]FastCopy Dashboard[/]"))
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.Cyan1);

        var stats = $"[green bold]Speed: {_viewModel.GlobalSpeed}[/]  |  " +
                    $"[white]Completed: {_viewModel.CompletedCount}[/]";

        if (_viewModel.FailedCount > 0)
        {
            stats += $"  |  [red bold]Failed: {_viewModel.FailedCount}[/]";
        }

        if (_viewModel.IsPaused)
        {
            stats += $"  |  [yellow bold][PAUSED][/]";
        }

        var rightPanel = new Panel(new Markup(stats))
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.Green);

        grid.AddRow(leftPanel, rightPanel);

        return grid;
    }

    private IRenderable RenderWorkersTable()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[yellow]Status[/]").Width(8).LeftAligned())
            .AddColumn(new TableColumn("[cyan]File[/]").Width(40).LeftAligned())
            .AddColumn(new TableColumn("[green]Progress[/]").Width(25).Centered())
            .AddColumn(new TableColumn("[white]%[/]").Width(7).RightAligned())
            .AddColumn(new TableColumn("[yellow]Speed[/]").Width(12).RightAligned());

        var workers = _viewModel.GetWorkerSnapshot();

        // Limit to visible rows (e.g., top 20)
        int maxVisible = Math.Min(workers.Count, 20);

        for (int i = 0; i < maxVisible; i++)
        {
            var worker = workers[i];
            
            var statusIcon = worker.Status switch
            {
                "Copying" => "[green]▶[/]",
                "Completed" => "[cyan]✓[/]",
                "Failed" => "[red]✗[/]",
                "Paused" => "[yellow]⏸[/]",
                _ => "[grey]•[/]"
            };

            var fileName = TruncatePath(worker.FileName, 38);
            var progressBar = BuildProgressBar(worker.Progress);
            var progressPercent = $"{worker.Progress:F1}%";
            var speed = worker.FormattedSpeed;

            table.AddRow(statusIcon, fileName, progressBar, progressPercent, speed);
        }

        if (workers.Count > maxVisible)
        {
            table.AddRow("[grey]...[/]", $"[grey]({workers.Count - maxVisible} more)[/]", "", "", "");
        }

        return new Panel(table)
            .Header("[bold]Active Transfers[/]")
            .Border(BoxBorder.Double)
            .BorderColor(Color.Blue);
    }

    private IRenderable RenderFooter()
    {
        var grid = new Grid();
        grid.AddColumn();

        // Overall progress bar
        var progressBar = new BarChart()
            .Width(60)
            .Label("[bold]Overall Progress[/]")
            .AddItem($"{_viewModel.Progress * 100:F1}%", _viewModel.Progress * 100, Color.Green);

        // Status message (escape to prevent markup parsing issues)
        var statusText = new Markup($"[white]{Markup.Escape(_viewModel.StatusMessage)}[/]");

        // Help text with keyboard controls
        var helpText = new Markup(
            "[grey]Controls: [cyan]P[/]/[cyan]Space[/]=Pause  " +
            "[cyan]H[/]=Hide  [cyan]+/-[/]=Speed  " +
            "[cyan]U[/]=Unlimited  [cyan]R[/]=Reset  " +
            "[cyan]Q[/]/[cyan]Esc[/]=Quit[/]");

        grid.AddRow(progressBar);
        grid.AddRow(statusText);
        grid.AddRow(helpText);

        return new Panel(grid)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
    }

    /// <summary>
    /// Render final state when transfers complete.
    /// </summary>
    public IRenderable RenderFinalState()
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(3),
                new Layout("Summary").Size(10),
                new Layout("Footer").Size(3)
            );

        layout["Header"].Update(RenderHeader());
        layout["Summary"].Update(RenderCompletionSummary());
        layout["Footer"].Update(new Panel(
            new Markup("[green bold]✓ Transfer Complete![/]\n[grey]Press any key to exit...[/]"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green));

        return layout;
    }

    private IRenderable RenderCompletionSummary()
    {
        var workers = _viewModel.GetWorkerSnapshot();
        int completed = workers.Count(w => w.Status == "Completed");
        int failed = workers.Count(w => w.Status == "Failed");
        
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn(new TableColumn("[cyan bold]Metric[/]").LeftAligned())
            .AddColumn(new TableColumn("[white]Value[/]").RightAligned());

        summary.AddRow("[cyan]Total Files[/]", $"{workers.Count}");
        summary.AddRow("[green]Completed[/]", $"{completed}");
        if (failed > 0)
        {
            summary.AddRow("[red]Failed[/]", $"{failed}");
        }
        summary.AddRow("[yellow]Total Transferred[/]", FormatBytes(_viewModel.TotalBytesTransferred));
        summary.AddRow("[cyan]Average Speed[/]", _viewModel.GlobalSpeed);

        return new Panel(summary)
            .Header("[bold]Transfer Summary[/]")
            .Border(BoxBorder.Double)
            .BorderColor(Color.Cyan1);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string TruncatePath(string path, int maxLength)
    {
        if (path.Length <= maxLength)
            return Markup.Escape(path);

        var fileName = Path.GetFileName(path);
        if (fileName.Length <= maxLength - 3)
            return Markup.Escape("..." + fileName);

        return Markup.Escape(path.Substring(0, maxLength - 3) + "...");
    }

    private static string BuildProgressBar(double progress)
    {
        // Spectre.Console will handle the actual rendering
        int filledWidth = (int)(progress / 100.0 * 20);
        int emptyWidth = 20 - filledWidth;
        
        return $"[green]{new string('█', filledWidth)}[/][grey]{new string('░', emptyWidth)}[/]";
    }
}
