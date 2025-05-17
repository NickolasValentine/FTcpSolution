using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Collections.Concurrent;
using System;

class Program
{
    // Порт, на котором сервер слушает новые подключения
    private const int Port = 9000;
    // Директория для хранения файлов на сервере
    private const string FilesDirectory = "ServerFiles";
    // Потокобезопасный словарь подключенных клиентов: key = clientId, value = ClientInfo
    private static readonly ConcurrentDictionary<string, ClientInfo> ConnectedClients = new();
    // FileSystemWatcher для отслеживания изменений в папке FilesDirectory
    private static FileSystemWatcher _fileWatcher;
    // Лок для защиты доступа к файлу лога
    private static readonly object LogLock = new object();
    // Путь к файлу лога
    private const string LogFile = "server.log";

    // Представление информации о клиенте
    public class ClientInfo
    {
        // Уникальный идентификатор клиента
        public string Id { get; } = Guid.NewGuid().ToString();
        // Основной поток для получения/отправки временных команд (UPLOAD, DOWNLOAD, LIST)
        public NetworkStream Stream { get; init; }
        // TcpClient для управления жизненным циклом
        public TcpClient Client { get; init; }
        // Отдельный поток для уведомлений (NOTIFICATION_CHANNEL)
        public NetworkStream NotificationStream { get; set; }
    }

    // Точка входа: инициализация логгера, файловой папки и запуск слушателя
    static async Task Main()
    {
        InitializeLogger();                   // Инициализируем логирование
        Directory.CreateDirectory(FilesDirectory); // Гарантируем, что папка для файлов существует
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();                      // Начинаем слушать новые TCP-подключения
        Logger($"Сервер запущен на порту {Port}");
        InitializeFileWatcher();               // Запускаем наблюдатель за локальными файлами

        // Основной цикл: асинхронно принимаем новых клиентов
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client);      // Обрабатываем подключение в фоне
        }
    }


    // Настройка заголовка новой сессии в логе
    private static void InitializeLogger()
    {
        Logger($"\n=== Новая сессия {DateTime.Now:dd.MM.yyyy HH:mm} ===");
    }

    // Метод логирования: запись в консоль и в файл
    private static void Logger(string message, bool isError = false)
    {
        var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{(isError ? "ERROR" : "INFO")}] {message}";
        Console.WriteLine(logEntry);
        lock (LogLock)
            File.AppendAllText(LogFile, logEntry + Environment.NewLine);
    }

    // Настройка FileSystemWatcher: по событиям изменения папки отправляем команду REFRESH всем клиентам
    private static void InitializeFileWatcher()
    {
        _fileWatcher = new FileSystemWatcher(FilesDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        // При создании, удалении, переименовании или изменении файла -> обновить список у клиентов
        _fileWatcher.Created += (s, e) => BroadcastCommand("REFRESH|");
        _fileWatcher.Deleted += (s, e) => BroadcastCommand("REFRESH|");
        _fileWatcher.Renamed += (s, e) => BroadcastCommand("REFRESH|");
        _fileWatcher.Changed += (s, e) => BroadcastCommand("REFRESH|");
        _fileWatcher.Error += (s, e) => Logger($"Ошибка FileSystemWatcher: {e.GetException().Message}", true);
    }

    // Обработка нового TCP-клиента: определяем тип канала и делегируем работу
    private static async Task HandleClientAsync(TcpClient client)
    {
        NetworkStream stream = null;
        ClientInfo clientInfo = null;

        try
        {
            stream = client.GetStream();
            var buffer = new byte[4096];

            // Читаем первую команду от клиента
            var bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0) return;

            var initialCommand = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var parts = initialCommand.Split('|');

            // Клиент запросил канал уведомлений
            if (parts[0] == "NOTIFICATION_CHANNEL")
            {
                clientInfo = new ClientInfo
                {
                    Stream = stream,
                    Client = client,
                    NotificationStream = stream
                };

                // Добавляем в список активных
                ConnectedClients.TryAdd(clientInfo.Id, clientInfo);
                Logger($"Новое подключение: {client.Client.RemoteEndPoint} | ID: {clientInfo.Id}");

                // Отправляем сгенерированный ID клиенту
                await SendResponseAsync(stream, $"CLIENT_ID|{clientInfo.Id}");

                // Уведомляем других клиентов о новом
                foreach (var existingClient in ConnectedClients.Values)
                {
                    if (existingClient.Id != clientInfo.Id)
                    {
                        await SendResponseAsync(existingClient.NotificationStream, $"CLIENT_CONNECTED|{clientInfo.Id}");
                    }
                }

                // Новому клиенту шлём список уже подключенных (кроме себя)
                foreach (var existingClient in ConnectedClients.Values)
                {
                    if (existingClient.Id != clientInfo.Id)
                    {
                        await SendResponseAsync(stream, $"CLIENT_CONNECTED|{existingClient.Id}");
                    }
                }

                // Вечно слушаем P2P-команды (SEND_TO_CLIENT)
                while (client.Connected)
                {
                    bytesRead = await stream.ReadAsync(buffer);
                    if (bytesRead == 0) break;

                    var command = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    parts = command.Split('|');

                    switch (parts[0])
                    {
                        case "SEND_TO_CLIENT":
                            // parts = ["SEND_TO_CLIENT", recipientId, fileName, fileSize]
                            var recipientId = parts[1];
                            var fileName = parts[2];
                            var fileSize = long.Parse(parts[3]);
                            // Обработка P2P в отдельном потоке
                            _ = Task.Run(() =>
                                HandleP2PTransfer(clientInfo.Id, recipientId, fileName, fileSize));
                            break;
                    }
                }
            }
            else
            {
                // Временные команды (UPLOAD, DOWNLOAD, LIST)
                await ProcessTemporaryCommand(stream, initialCommand);
            }
        }
        catch (Exception ex)
        {
            Logger($"Ошибка: {ex.Message}", true);
        }
        finally
        {
            // При отключении очищаем
            if (clientInfo != null)
            {
                ConnectedClients.TryRemove(clientInfo.Id, out _);
                BroadcastCommand($"CLIENT_DISCONNECTED|{clientInfo.Id}");
            }
            client.Dispose();
        }
    }

    // Обработка команд без постоянного канала
    private static async Task ProcessTemporaryCommand(NetworkStream stream, string command)
    {
        var parts = command.Split('|');
        try
        {
            switch (parts[0])
            {
                case "UPLOAD":
                case "FORCE_UPLOAD":
                    await HandleUploadAsync(stream, parts, parts[0] == "FORCE_UPLOAD");
                    break;
                case "DOWNLOAD":
                    if (parts.Length > 1)
                        await HandleDownloadAsync(stream, parts[1]);
                    break;
                case "LIST":
                    await HandleListAsync(stream);
                    break;
                default:
                    await SendResponseAsync(stream, "UNKNOWN_COMMAND");
                    break;
            }
        }
        finally
        {
            stream.Close();
        }
    }

    // Приём файла от клиента (UPLOAD)
    private static async Task HandleUploadAsync(NetworkStream stream, string[] parts, bool forceOverwrite)
    {
        string fileName = parts[1];
        string filePath = Path.Combine(FilesDirectory, fileName);
        long fileSize = long.Parse(parts[2]);

        // Если файл уже есть — по заголовку FORCE_UPLOAD удаляем старый
        if (File.Exists(filePath))
        {
            if (forceOverwrite)
            {
                File.Delete(filePath);
            }
            else
            {
                await SendResponseAsync(stream, "FILE_EXISTS");
                return;
            }
        }

        await SendResponseAsync(stream, "HEADER_ACK"); // Готовы принимать тело

        using (var fs = new FileStream(filePath, FileMode.Create))
        {
            long totalReceived = 0;
            var buffer = new byte[8192];

            while (totalReceived < fileSize)
            {
                var bytesRead = await stream.ReadAsync(buffer);
                await fs.WriteAsync(buffer, 0, bytesRead);
                totalReceived += bytesRead;
            }
        }

        await SendResponseAsync(stream, "ACK");
        BroadcastCommand($"REFRESH|"); // Уведомляем клиентов об обновлении
    }

    // Отдача файла клиенту (DOWNLOAD)
    private static async Task HandleDownloadAsync(NetworkStream stream, string fileName)
    {
        string filePath = Path.Combine(FilesDirectory, fileName);
        if (!File.Exists(filePath)) return;

        var fileInfo = new FileInfo(filePath);
        await SendResponseAsync(stream, $"SIZE|{fileInfo.Length}"); // Сначала шлём размер

        using (var fs = File.OpenRead(filePath))
        {
            var buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = await fs.ReadAsync(buffer)) > 0)
                await stream.WriteAsync(buffer, 0, bytesRead);
        }

        await SendResponseAsync(stream, "ACK");
    }

    // Отправка списка файлов (LIST)
    private static async Task HandleListAsync(NetworkStream stream)
    {
        try
        {
            var files = Directory.GetFiles(FilesDirectory)
                .Select(p => new FileInfo(p))
                .Select(f => $"{f.Name},{f.Length},{f.Extension},{f.LastWriteTime:yyyy-MM-dd HH:mm:ss}");

            var response = files.Any()
                ? string.Join("|", files) + "|ACK"
                : "ACK";

            var bytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Logger($"Ошибка списка: {ex.Message}", true);
        }
    }

    // Отправка строки с \n-разделителем
    private static async Task SendResponseAsync(NetworkStream stream, string response)
    {
        try
        {
            // Добавляем разделитель '\n' для чёткого разделения команд
            var bytes = Encoding.UTF8.GetBytes(response + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Logger($"Ошибка отправки: {ex.Message}", true);
        }
    }

    // Широковещательная команда всем подключённым клиентам
    private static void BroadcastCommand(string command)
    {
        var commandBytes = Encoding.UTF8.GetBytes(command + "\n");
        foreach (var client in ConnectedClients.Values.ToList())
        {
            if (client.NotificationStream != null && client.NotificationStream.CanWrite)
            {
                try
                {
                    client.NotificationStream.WriteAsync(commandBytes, 0, commandBytes.Length);
                }
                catch { /* Игнорируем ошибки */ }
            }
        }
    }

    // Логика P2P-передачи: получает соединения, ретранслирует поток и ждёт ACK
    static async Task HandleP2PTransfer(string senderId, string recipientId, string fileName, long fileSize)
    {
        // По senderId и recipientId находим ClientInfo
        if (!ConnectedClients.TryGetValue(senderId, out var senderInfo) ||
            !ConnectedClients.TryGetValue(recipientId, out var recipientInfo))
        {
            Logger($"P2P: клиент не найден {senderId} или {recipientId}", true);
            return;
        }
        var senderStream = senderInfo.NotificationStream;
        var recipientStream = recipientInfo.NotificationStream;

        // Открываем временный listener на свободном порту
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Шлём порт обеим сторонам
        await SendLineAsync(senderStream, $"DATA_PORT|{port}");
        await SendLineAsync(recipientStream, $"DATA_PORT|{port}");

        // Оповещаем получателя о входящем файле
        await SendLineAsync(recipientStream,
                            $"INCOMING_FILE|{senderId}|{fileName}|{fileSize}");

        // Ждём два соединения: от отправителя и получателя
        var conn1 = await listener.AcceptTcpClientAsync();
        var conn2 = await listener.AcceptTcpClientAsync();

        // Читаем ID обеих сторон
        var ns1 = conn1.GetStream();
        var ns2 = conn2.GetStream();
        var id1 = await ReadClientIdAsync(ns1);
        var id2 = await ReadClientIdAsync(ns2);

        if ((id1 != senderId || id2 != recipientId) && (id1 != recipientId || id2 != senderId))
        {
            Logger($"P2P: получены ID: [{id1}] и [{id2}], ожидались: [{senderId}] и [{recipientId}]", true);
        }

        // Разбираем, кто sender, кто recipient
        TcpClient dataSender, dataReceiver;
        if (id1 == senderId && id2 == recipientId)
        {
            dataSender = conn1;
            dataReceiver = conn2;
        }
        else if (id2 == senderId && id1 == recipientId)
        {
            dataSender = conn2;
            dataReceiver = conn1;
        }
        else
        {
            Logger("P2P: неверные clientId на data-соединении", true);
            listener.Stop();
            return;
        }

        try
        {
            // Relay
            var sendNs = dataSender.GetStream();
            var recvNs = dataReceiver.GetStream();

            // 1. читаем clientId
            var idSender = await ReadClientIdAsync(sendNs);
            var idReceiver = await ReadClientIdAsync(recvNs);

            // Ретрансляция данных между sendNs и recvNs
            var buffer = new byte[8192];
            int read;
            while ((read = await sendNs.ReadAsync(buffer)) > 0)
            {
                await recvNs.WriteAsync(buffer, 0, read);
            }
            await recvNs.FlushAsync();

            // Оповещение об окончании передачи
            dataSender.Client.Shutdown(SocketShutdown.Send);

            // Ожидание ACK от получателя
            var ackSb = new StringBuilder();
            var buf = new byte[1];
            while (true)
            {
                int readACK = await recvNs.ReadAsync(buf, 0, 1);
                if (readACK == 0) break;
                char c = (char)buf[0];
                if (c == '\n') break;
                ackSb.Append(c);
            }

            if (ackSb.ToString().Trim() != "ACK")
            {
                Logger($"Подтверждение не получено. Получено: {ackSb}.", true);
            }
            else
            {
                Logger($"Получено:{ackSb}.");
            }

            // Уведомляем обе стороны о завершении
            await SendLineAsync(senderStream, "TRANSFER_COMPLETE");
            await SendLineAsync(recipientStream, "TRANSFER_COMPLETE");
            dataSender.Close();
            dataReceiver.Close();
            listener.Stop();
        }
        catch (Exception ex)
        {
            Logger($"Ошибка P2P-релеи: {ex.Message}", true);
        }
    }

    // Отправка строки с разделителем и переводом строки
    static Task SendLineAsync(NetworkStream s, string line)
    {
        var buf = Encoding.UTF8.GetBytes(line + "\n");
        return s.WriteAsync(buf, 0, buf.Length);
    }

    // Считывает clientId до \n
    private static async Task<string> ReadClientIdAsync(NetworkStream ns)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            int n = await ns.ReadAsync(buf, 0, 1);
            if (n == 0) throw new IOException("Соединение закрыто до получения ID");
            char c = (char)buf[0];
            if (c == '\n') break;
            if (c != '\r') sb.Append(c);
        }
        return sb.ToString();
    }
}