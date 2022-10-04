using Microsoft.Exchange.WebServices.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var serviceProvider = ConfigureLogger();
var logger = CreateLogger(serviceProvider);
var configurationBuilder = new ConfigurationBuilder();
var config = configurationBuilder.AddJsonFile("config.json").Build();

logger.LogInformation($"App started");
var username = config["username"];
var service = new ExchangeService
{
    Credentials = new WebCredentials(username, config["password"]),
    Url = new Uri(config["host"]),
};

var newEmailSubscription = await service.SubscribeToStreamingNotificationsOnAllFolders(EventType.NewMail);
var connection = new StreamingSubscriptionConnection(service, 30);
connection.AddSubscription(newEmailSubscription);
connection.OnNotificationEvent += async (sender, eventArgs) =>
{
    foreach (var _ in eventArgs.Events)
    {
        var items = await service.FindItems(new FolderId(WellKnownFolderName.Inbox, new Mailbox(username))
            , new SearchFilter.SearchFilterCollection(LogicalOperator.And,
                new SearchFilter.ContainsSubstring(ItemSchema.Subject, config["subject"]),
                new SearchFilter.IsEqualTo(EmailMessageSchema.IsRead, false)),
            new ItemView(15)
        );
        items.ToList().ForEach(async item =>
        {
            var itemId = (EmailMessage.Bind(service, item.Id)).Result.Id;
            var message = EmailMessage.Bind(service, itemId,
                new PropertySet(BasePropertySet.FirstClassProperties,
                    new ExtendedPropertyDefinition(0x1013, MapiPropertyType.Binary))).Result;
            logger.LogInformation(
                $"New email - Subject: {message.Subject} Sender: {message.Sender.Address} Body: {message.Body.Text}");


            message = EmailMessage.Bind(service, itemId,
                new PropertySet(BasePropertySet.FirstClassProperties, ItemSchema.Attachments)).Result;
            foreach (var attachment in message.Attachments)
            {
                if (attachment is FileAttachment fileAttachment)
                {
                    await fileAttachment.Load();
                    var path = Path.Combine(config["path"], attachment.Name);
                    await using var fs = new FileStream(path, FileMode.Create);
                    await using var bw = new BinaryWriter(fs);
                    bw.Write(fileAttachment.Content);
                    logger.LogInformation($"File attachment downloaded: {path}");
                }
            }

            message.IsRead = true;
            message.Subject += "_processed";
            await message.Update(ConflictResolutionMode.AutoResolve);
            logger.LogInformation($"Set message as read");
        });
    }
};
connection.OnDisconnect += (sender, eventArgs) => { connection.Open(); };

connection.Open();
Console.ReadLine();

static ServiceProvider ConfigureLogger()
{
    var serviceProvider = new ServiceCollection().AddLogging(x => x.AddLog4Net())
        .AddLogging(_ => _.AddConsole()).BuildServiceProvider();
    return serviceProvider;
}

static ILogger<Program> CreateLogger(IServiceProvider serviceProvider)
{
    return serviceProvider.GetService<ILoggerFactory>().CreateLogger<Program>();
}