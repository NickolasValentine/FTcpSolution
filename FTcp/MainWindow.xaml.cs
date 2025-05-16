using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Policy;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FTcp
{
    public partial class MainWindow : Window
    {
        private string serverAddress;
        private int serverPort;
        private bool isConnected = false;
        private string clientId;
        private const string ClientFilesDir = "ClientFiles";

        public ObservableCollection<FileItem> LocalFiles { get; } = new ObservableCollection<FileItem>();
        public ObservableCollection<FileItem> ServerFiles { get; } = new ObservableCollection<FileItem>();
        public ObservableCollection<ClientInfo> ConnectedClients { get; } = new ObservableCollection<ClientInfo>();

        private TcpClient notificationClient;
        private NetworkStream notificationStream;
        private CancellationTokenSource _updateCts;
        private FileSystemWatcher localFileWatcher;
        private readonly object logLock = new object();
        private const string LogFile = "client.log";

        private TaskCompletionSource<int> _dataPortTcs;
        private int _lastPort;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Инициализация папки и наблюдателя за файлами
            Directory.CreateDirectory(ClientFilesDir);
            InitializeLocalFileWatcher();
            LoadLocalFiles();
            Logger("=== Новая сессия ===");
        }

        private void Logger(string message, bool isError = false)
        {
            var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{(isError ? "ERROR" : "INFO")}] {message}";
            Console.WriteLine(logEntry);
            lock (logLock)
                File.AppendAllText(LogFile, logEntry + Environment.NewLine);
        }

        private void InitializeLocalFileWatcher()
        {
            localFileWatcher = new FileSystemWatcher(ClientFilesDir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            localFileWatcher.Created += (s, e) => Dispatcher.Invoke(LoadLocalFiles);
            localFileWatcher.Deleted += (s, e) => Dispatcher.Invoke(LoadLocalFiles);
            localFileWatcher.Renamed += (s, e) => Dispatcher.Invoke(LoadLocalFiles);
        }

        private void LoadLocalFiles()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    LocalFiles.Clear();
                    foreach (var file in Directory.GetFiles(ClientFilesDir))
                    {
                        var fi = new FileInfo(file);
                        fi.Refresh(); // Обновляем информацию о файле
                        LocalFiles.Add(new FileItem
                        {
                            FileName = fi.Name,
                            FileSize = fi.Length,
                            FileType = fi.Extension,
                            LastModified = fi.LastWriteTime
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Logger($"Ошибка загрузки локальных файлов: {ex.Message}", true);
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _updateCts?.Cancel();
                notificationStream?.Dispose();
                notificationClient?.Dispose();
                isConnected = false;
                LocalFilesListView.IsEnabled = false;
                ServerFilesListView.IsEnabled = false;
                ServerFiles.Clear();
                ConnectedClients.Clear();

                if (!int.TryParse(PortTextBox.Text, out serverPort) || serverPort < 1 || serverPort > 65535)
                {
                    MessageBox.Show("Некорректный порт!");
                    return;
                }

                serverAddress = IpTextBox.Text;
                isConnected = true;
                LocalFilesListView.IsEnabled = true;
                ServerFilesListView.IsEnabled = true;

                StartListeningForUpdates();
                await LoadServerFiles().ConfigureAwait(false);

                MessageBox.Show("Подключено успешно!");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка подключения: {ex.Message}");
                Logger($"Ошибка подключения: {ex.Message}", true);
            }
        }

        private void StartListeningForUpdates()
        {
            _updateCts = new CancellationTokenSource();
            Task.Run(() => ListenForUpdatesAsync(_updateCts.Token), _updateCts.Token);
        }

        private async Task ListenForUpdatesAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    notificationClient?.Dispose();
                    notificationClient = new TcpClient();
                    await notificationClient.ConnectAsync(serverAddress, serverPort, token);
                    notificationStream = notificationClient.GetStream();

                    var initCommand = Encoding.UTF8.GetBytes("NOTIFICATION_CHANNEL");
                    await notificationStream.WriteAsync(initCommand, 0, initCommand.Length, token);

                    var buffer = new byte[128];
                    var bytesRead = await notificationStream.ReadAsync(buffer, 0, buffer.Length, token);
                    var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    if (response.StartsWith("CLIENT_ID|"))
                    {
                        clientId = response.Split('|')[1];
                        // Обновите текст в UI
                        Dispatcher.Invoke(() => ClientIdText.Text = $"ID: {clientId}");
                        await notificationStream.WriteAsync(Encoding.UTF8.GetBytes("NOTIFICATION_ACK"), token);

                        // ... остальной код ...
                        var mainBuffer = new byte[4096];
                        StringBuilder dataBuffer = new StringBuilder();

                        while (notificationClient.Connected && !token.IsCancellationRequested)
                        {
                            bytesRead = await notificationStream.ReadAsync(mainBuffer, 0, mainBuffer.Length, token);
                            if (bytesRead == 0) break;

                            dataBuffer.Append(Encoding.UTF8.GetString(mainBuffer, 0, bytesRead));
                            var data = dataBuffer.ToString();

                            // Разделяем команды по '\n'
                            var commands = data.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var cmd in commands)
                            {
                                ProcessServerCommand(cmd);
                            }

                            // Оставляем необработанную часть в буфере
                            dataBuffer.Clear();
                            int lastSeparatorIndex = data.LastIndexOf('\n');
                            if (lastSeparatorIndex != -1 && lastSeparatorIndex < data.Length - 1)
                            {
                                dataBuffer.Append(data.Substring(lastSeparatorIndex + 1));
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Logger("Таймаут операции", true);
                    await Dispatcher.InvokeAsync(() => MessageBox.Show("Сервер не отвечает."));
                }
                catch (OperationCanceledException ex)
                {
                    // Если это отмена не токеном (какая-то внутренняя таймаутная отмена),
                    // то мможно логировать или показывать своё сообщение:
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    Logger($"Сервер недоступен: {ex.Message}", true);
                    await Dispatcher.InvokeAsync(() => MessageBox.Show("Сервер недоступен."));
                    await Task.Delay(5000, token);
                }
                catch (Exception ex)
                {
                    // Отлавливаем все прочие ошибки, но игнорируем повторные OperationCanceledException
                    if (ex is OperationCanceledException) return;
                    Dispatcher.Invoke(() => MessageBox.Show($"Ошибка связи: {ex.Message}"));
                }
                finally
                {
                    notificationClient?.Dispose();
                    notificationClient = null;
                    notificationStream = null;
                }
            }
        }

        // Переписанный метод обработки команд от сервера:
        private void ProcessServerCommand(string command)
        {
            var parts = command.Split('|');
            if (parts.Length < 1) return;

            switch (parts[0])
            {
                case "DATA_PORT":
                    if (int.TryParse(parts[1], out var p))
                    {
                        _lastPort = p;
                        _dataPortTcs?.TrySetResult(p);
                    }
                    break;

                case "INCOMING_FILE":
                    // Распарсили senderId, fileName, fileSize
                    var sid = parts[1];
                    var fn = parts[2];
                    var sz = long.Parse(parts[3]);

                    // Новый TCS для приёма
                    _dataPortTcs = new TaskCompletionSource<int>();
                    if (_lastPort != 0)
                        _dataPortTcs.TrySetResult(_lastPort);

                    // Запускаем приём
                    Dispatcher.Invoke(async () =>
                    {
                        if (MessageBox.Show($"Принять {fn} от {sid}?", "", MessageBoxButton.YesNo)
                            == MessageBoxResult.Yes)
                        {
                            await ReceiveFileAsync(sid, fn, sz);
                        }
                    });
                    break;
                case "REFRESH":
                    // Обновляем список файлов сервера в UI-потоке
                    Dispatcher.Invoke(async () => await LoadServerFiles());
                    break;
                case "TRANSFER_COMPLETE":
                    Dispatcher.Invoke(() => MessageBox.Show("Передача завершена"));
                    break;
                case "CLIENT_CONNECTED":
                    var newId = parts[1];
                    // Обязательно в UI-потоке
                    Dispatcher.Invoke(() =>
                    {
                        if (!ConnectedClients.Any(c => c.Id == newId) && newId != clientId)
                            ConnectedClients.Add(new ClientInfo { Id = newId });
                    });
                    break;
                case "CLIENT_DISCONNECTED":
                    var remId = parts[1];
                    Dispatcher.Invoke(() =>
                    {
                        var item = ConnectedClients.FirstOrDefault(c => c.Id == remId);
                        if (item != null) ConnectedClients.Remove(item);
                    });
                    break;
            }
        }

        private async Task ReceiveFileAsync(string fromId, string fileName, long fileSize)
        {
            try
            {
                // 1) ждём порт (TCS)
                int port = await _dataPortTcs.Task;

                // 2) подключаемся и шлём clientId
                using var dc = new TcpClient();
                await dc.ConnectAsync(serverAddress, port);
                using var ns = dc.GetStream();
                await ns.WriteAsync(Encoding.UTF8.GetBytes(clientId + "\n"));

                // 3) создаём файл и читаем ровно fileSize байт

                string outPath = Path.Combine(ClientFilesDir, fileName);
                using (var fs = File.Create(outPath))
                {
                    var buffer = new byte[8192];
                    long remaining = fileSize;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = await ns.ReadAsync(buffer, 0, toRead);
                        if (read == 0) break;
                        await fs.WriteAsync(buffer, 0, read);
                        remaining -= read;
                    }

                    if (remaining != 0)
                    {
                        throw new IOException($"Не хватает {remaining} байт.");
                    }

                    // Отправляем подтверждение серверу
                    await ns.WriteAsync(Encoding.UTF8.GetBytes("ACK\n"));
                    await ns.FlushAsync();
                    dc.Client.Shutdown(SocketShutdown.Send);
                }

                // Обновляем UI
                Dispatcher.Invoke(() =>
                {
                    LoadLocalFiles();
                    LocalFilesListView.Items.Refresh();
                    MessageBox.Show($"Получен {fileName} ({fileSize} байт) от {fromId}");
                });
            }
            catch (Exception ex)
            {
                Logger($"Ошибка приёма файла: {ex.Message}", true);
                Dispatcher.Invoke(() => MessageBox.Show($"Ошибка приёма: {ex.Message}"));
            }
        }

        private async void HandleIncomingFile(string senderId, string fileName, long fileSize)
        {
            var result = MessageBox.Show(
                $"Принять файл {fileName} ({fileSize} байт) от клиента {senderId}?",
                "Входящий файл", MessageBoxButton.YesNo);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                int dataPort = (int)Application.Current.Properties["DATA_PORT"];
                var filePath = Path.Combine(ClientFilesDir, fileName);

                using var dataClient = new TcpClient();
                await dataClient.ConnectAsync(serverAddress, dataPort);
                using var dataStream = dataClient.GetStream();

                // **ШАГ B**: первым делом отсылаем clientId
                var idLine = Encoding.UTF8.GetBytes(clientId + "\n");
                await dataStream.WriteAsync(idLine, 0, idLine.Length);

                // Затем читаем данные в файл
                using var fs = File.Create(filePath);
                var buffer = new byte[8192];
                long totalRead = 0;
                while (totalRead < fileSize)
                {
                    int toRead = (int)Math.Min(buffer.Length, fileSize - totalRead);
                    int read = await dataStream.ReadAsync(buffer, 0, toRead);
                    if (read == 0) break;
                    await fs.WriteAsync(buffer, 0, read);
                    totalRead += read;
                }

                // Отправляем подтверждение серверу
                await notificationStream.WriteAsync(Encoding.UTF8.GetBytes("ACK_TRANSFER\n"));

                LoadLocalFiles();
                MessageBox.Show("Файл принят!");
            }
            catch (Exception ex)
            {
                Logger($"Ошибка: {ex.Message}", true);
                MessageBox.Show($"Ошибка: {ex.Message}");
            }
        }

        private async Task LoadServerFiles()
        {
            try
            {
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(serverAddress, serverPort);
                    using (var stream = client.GetStream())
                    {
                        var request = Encoding.UTF8.GetBytes("LIST|");
                        await stream.WriteAsync(request, 0, request.Length);

                        var buffer = new byte[4096];
                        int totalBytesRead = 0;

                        do
                        {
                            var bytesRead = await stream.ReadAsync(buffer, totalBytesRead, buffer.Length - totalBytesRead);
                            totalBytesRead += bytesRead;
                        }
                        while (totalBytesRead > 0 && !Encoding.UTF8.GetString(buffer, 0, totalBytesRead).Contains("ACK"));

                        var response = Encoding.UTF8.GetString(buffer, 0, totalBytesRead);
                        Dispatcher.Invoke(() =>
                        {
                            ServerFiles.Clear();
                            foreach (var entry in response.Split('|').Where(e => e != "ACK"))
                            {
                                var parts = entry.Split(',');
                                if (parts.Length == 4 && long.TryParse(parts[1], out var size))
                                {
                                    ServerFiles.Add(new FileItem
                                    {
                                        FileName = parts[0],
                                        FileSize = size,
                                        FileType = parts[2],
                                        UploadDate = DateTime.ParseExact(parts[3], "yyyy-MM-dd HH:mm:ss", null)
                                    });
                                }
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger($"Ошибка загрузки списка файлов: {ex.Message}", true);
                await Dispatcher.InvokeAsync(() => MessageBox.Show($"Ошибка загрузки списка: {ex.Message}"));
            }
        }

        private async void UploadToServer_Click(object sender, RoutedEventArgs e)
        {
            var fileItem = LocalFilesListView.SelectedItem as FileItem;
            if (fileItem == null) return;

            var path = Path.Combine(ClientFilesDir, fileItem.FileName);
            var fileBytes = File.ReadAllBytes(path);
            bool retryWithForce = false;

            do
            {
                retryWithForce = false;
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(serverAddress, serverPort);
                    using var stream = client.GetStream();

                    // выбрали заголовок
                    var cmdType = retryWithForce ? "FORCE_UPLOAD" : "UPLOAD";
                    var header = $"{cmdType}|{fileItem.FileName}|{fileBytes.Length}";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(header));

                    // ждём ответа
                    var rspBuf = new byte[128];
                    int read = await stream.ReadAsync(rspBuf);
                    var response = Encoding.UTF8.GetString(rspBuf, 0, read).Trim();

                    if (response == "FILE_EXISTS" && !retryWithForce)
                    {
                        // просто переподключаемся и пробуем перезаписать
                        retryWithForce = true;
                        continue;
                    }

                    // дальше идёт запись тела
                    await stream.WriteAsync(fileBytes, 0, fileBytes.Length);
                    // можно ждать «HEADER_ACK» (как в загрузке), но сервер у вас сразу идёт в приём
                    MessageBox.Show("Файл успешно загружен!");
                    await LoadServerFiles();
                }
                catch (Exception ex)
                {
                    Logger($"Ошибка загрузки файла: {ex.Message}", true);
                    MessageBox.Show($"Ошибка загрузки: {ex.Message}");
                }
            } while (retryWithForce);
        }

        private async void SendToClient_Click(object sender, RoutedEventArgs e)
        {
            var fi = LocalFilesListView.SelectedItem as FileItem;
            var ci = ClientsComboBox.SelectedItem as ClientInfo;
            if (fi == null || ci == null)
            {
                MessageBox.Show("Выберите файл и получателя");
                return;
            }

            try
            {
                // Новый TCS для этой сессии
                _dataPortTcs = new TaskCompletionSource<int>();
                _lastPort = 0;

                // Отправляем команду
                var path = Path.Combine(ClientFilesDir, fi.FileName);
                var sz = new FileInfo(path).Length;
                var cmd = $"SEND_TO_CLIENT|{ci.Id}|{fi.FileName}|{sz}\n";
                await notificationStream.WriteAsync(Encoding.UTF8.GetBytes(cmd));

                // Ждём порт
                int port = await _dataPortTcs.Task;

                // Подключаемся и шлём clientId
                using var dc = new TcpClient();
                await dc.ConnectAsync(serverAddress, port);
                using var ns = dc.GetStream();
                await ns.WriteAsync(Encoding.UTF8.GetBytes(clientId + "\n"));

                // Копируем файл в ns
                using var fs = File.OpenRead(path);
                await fs.CopyToAsync(ns);
            }
            catch (Exception ex)
            {
                Logger($"Ошибка отправки файла: {ex.Message}", true);
                MessageBox.Show($"Ошибка отправки: {ex.Message}");
            }
        }

        private async void ServerFilesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var fileItem = ServerFilesListView.SelectedItem as FileItem;
            if (fileItem == null) return;

            try
            {
                var filePath = Path.Combine(ClientFilesDir, fileItem.FileName);

                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(serverAddress, serverPort);
                    using (var stream = client.GetStream())
                    {
                        var request = $"DOWNLOAD|{fileItem.FileName}";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(request));

                        var sizeBuffer = new byte[128];
                        var bytesRead = await stream.ReadAsync(sizeBuffer);
                        var sizeHeader = Encoding.UTF8.GetString(sizeBuffer, 0, bytesRead);
                        var fileSize = long.Parse(sizeHeader.Split('|')[1]);

                        using (var fs = File.Create(filePath))
                        {
                            var buffer = new byte[8192];
                            long totalRead = 0;

                            while (totalRead < fileSize)
                            {
                                var bytesToRead = (int)Math.Min(buffer.Length, fileSize - totalRead);
                                bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);
                                await fs.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;
                            }
                        }

                        LoadLocalFiles();
                        MessageBox.Show("Файл успешно скачан!");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger($"Ошибка скачивания файла: {ex.Message}", true);
                MessageBox.Show($"Ошибка скачивания: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _updateCts?.Cancel();
            notificationStream?.Dispose();
            notificationClient?.Dispose();
            base.OnClosed(e);
        }
    }

    public class FileItem : INotifyPropertyChanged
    {
        private string _fileName;
        public string FileName
        {
            get => _fileName;
            set { _fileName = value; OnPropertyChanged(); }
        }

        private long _fileSize;
        public long FileSize
        {
            get => _fileSize;
            set { _fileSize = value; OnPropertyChanged(); }
        }

        private string _fileType;
        public string FileType
        {
            get => _fileType;
            set { _fileType = value; OnPropertyChanged(); }
        }

        private DateTime _lastModified;
        public DateTime LastModified
        {
            get => _lastModified;
            set { _lastModified = value; OnPropertyChanged(); }
        }

        private DateTime _uploadDate;
        public DateTime UploadDate
        {
            get => _uploadDate;
            set { _uploadDate = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ClientInfo
    {
        public string Id { get; set; }
    }
}