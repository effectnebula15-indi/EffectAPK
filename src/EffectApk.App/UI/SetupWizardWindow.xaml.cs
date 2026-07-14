using System.Windows;
using EffectApk.Core;

namespace EffectApk.UI;

/// <summary>Единственный «настоящий» UI приложения: одноразовый мастер загрузки Android-рантайма.</summary>
public partial class SetupWizardWindow : Window
{
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private bool _running;

    public SetupWizardWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        var missing = new RuntimeBootstrapper(settings).Validate();
        if (missing.Count > 0)
            StatusText.Text = "Не хватает компонентов: " + string.Join(", ", missing);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        _running = true;
        _cts = new CancellationTokenSource();

        var progress = new Progress<SetupProgress>(p =>
        {
            if (p.Fraction is { } fraction)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = fraction;
            }
            else if (!p.Detail)
            {
                Progress.IsIndeterminate = true;
            }

            if (p.Message.Length > 0)
            {
                if (!p.Detail) StatusText.Text = p.Message;
                AppendLog(p.Message);
            }
        });

        try
        {
            await new RuntimeBootstrapper(_settings).RunAsync(progress, _cts.Token);
            _running = false;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _running = false;
            StatusText.Text = "Установка отменена.";
            StartButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _running = false;
            Logger.Error("Установка рантайма не удалась", ex);
            AppendLog("ОШИБКА: " + ex.Message);
            StatusText.Text = "Установка не удалась — подробности выше и в логе.";
            StartButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            return;
        }
        DialogResult = false;
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
