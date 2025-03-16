using System;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using static WebApplication2.DbHelpers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddAntiforgery(options =>
{
    // Отключаем анти-фальсификацию для всех запросов
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.None;
    options.Cookie.IsEssential = true;
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.AllowAnyOrigin()  // Разрешить запросы с любого источника
            .AllowAnyMethod()  // Разрешить любые HTTP-методы
            .AllowAnyHeader(); // Разрешить любые заголовки
    });
});



var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
    context.Response.Headers.Append("Access-Control-Allow-Methods", "*");
    context.Response.Headers.Append("Access-Control-Allow-Headers", "*");
    // Do work that can write to the Response.
    await next.Invoke();
    // Do logging or other work that doesn't write to the Response.
});

app.UseCors("AllowAll");

// Можно указать другой каталог для статических файлов, например:
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot\\files")),
    RequestPath = "/files"
});
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
//
// app.UseHttpsRedirection();

app.MapPost("/orders/result", async (UploadOrderResult model) =>
{
    var orderCreatorId = await Connection.ExecuteScalarAsync<int>(
        "SELECT userId FROM orders WHERE orderId = @orderId " ,
        new { orderId = model.OrderId});
    await Connection.ExecuteAsync("UPDATE orders SET fileResultPath = @filePath, STATUS = 'РЕЗУЛЬТАТ' WHERE orderId = @orderId",
        new { filePath = model.FilePath, orderId =  model.OrderId });
    await Connection.ExecuteAsync(
        "INSERT INTO users_orders_actions(orderId, creatorOrderId, fromUserId, message, actionDate, actionName, toUserId) " +
        "   VALUES (@orderId, @orderCreatorId, @userId, \"Загрузил результат\", @ActionDate, \"РЕЗУЛЬТАТ\", @orderCreatorId)", 
        new { orderId =  model.OrderId, userId = model.UserId, orderCreatorId, ActionDate = DateTime.Now.ToString("dd.MM.yyyy hh:mm") });
});

app.MapPost("/orders/result/accept", async (int userId, int orderId) =>
{
    var orderData = await Connection.QuerySingleAsync<OrderAcceptResultData>(
        "SELECT userId, userRespondedId FROM orders WHERE orderId = @orderId " ,
        new { orderId});
    await Connection.ExecuteAsync("UPDATE orders SET STATUS = 'ГОТОВО' WHERE orderId = @orderId",
        new { orderId });
    await Connection.ExecuteAsync(
        "INSERT INTO users_orders_actions(orderId, creatorOrderId, fromUserId, message, actionDate, actionName, toUserId) " +
        "   VALUES (@orderId, @orderCreatorId, @userId, \"Принял результат\", @ActionDate, \"ГОТОВО\", @toUserId)", 
        new { orderId, userId, orderCreatorId = orderData.UserId, toUserId = orderData.UserRespondedId, ActionDate = DateTime.Now.ToString("dd.MM.yyyy hh:mm") });
});

app.MapPost("/register", async (RegisterData data) =>
{
    var newData = data with
    {
        Password = HashPassword(data.Password)
    };
    var userExists = await Connection.ExecuteScalarAsync<int>(
        "SELECT COUNT(1) FROM users WHERE login = @Login OR phoneNumber = @PhoneNumber",
        newData) > 0;
    if (userExists)
    {
        return Results.BadRequest(new
        {
            Message = "Пользователь существует"
        });
    }
    var id = await Connection.ExecuteScalarAsync<int>(
        "INSERT INTO users(login, passwordHash, service, phoneNumber, status)" +
        "VALUES (@Login, @Password, @Service, @PhoneNumber, \"СВОБОДЕН\");" +
        "SELECT last_insert_rowid();", newData);
    return Results.Ok(new
    {
        Id = id
    });
    
}).DisableAntiforgery();
app.MapGet("/profile/{id:int}", async (int id) =>
{
    var profileData = await Connection.QuerySingleAsync<ProfileData>("SELECT login, portfolio, status, service, avatarPath FROM users WHERE userId = @id AND isDeleted = FALSE", new { id });
    var orders = await Connection.QueryAsync<ProfileOrder>(@"
        SELECT
            orders.title,
            orders.status,
            orders.dateRange,
            orders.orderId,
            orders.fileResultPath,
        --  orders.userId,
        --  orders.userRespondedId,
        --  anotherUser.login,
            CASE
                WHEN orders.userId = @id AND orders.userRespondedId IS NOT NULL AND orders.status IS NOT 'ГОТОВО' THEN 'ДЕЛАЕТ'
                WHEN orders.userId = @id AND orders.userRespondedId IS NOT NULL AND orders.status IS 'ГОТОВО' THEN 'ЗАКОНЧИЛ'
                WHEN orders.userRespondedId = @id AND orders.status IS NOT 'ГОТОВО' THEN 'ЖДЕТ'
                WHEN orders.userRespondedId = @id AND orders.status IS 'ГОТОВО' THEN 'ПРИНЯЛ'
                WHEN orders.userId = @id AND orders.userRespondedId IS NULL THEN 'ОЖИДАЕТ ФРИЛАНСЕРА'
                END AS userAction,
            CASE
                WHEN orders.userId = @id AND orders.userRespondedId IS NOT NULL THEN (SELECT login FROM users WHERE users.userId = orders.userRespondedId)
                WHEN orders.userRespondedId = @id OR orders.userRespondedId IS NULL THEN (SELECT login FROM users WHERE users.userId = orders.userId)
            END AS login,
            orders.userRespondedId = @id AND orders.status IS 'ВЗЯТ' AS isUploadable,
            orders.userId = @id AND orders.status IS 'РЕЗУЛЬТАТ' AND orders.fileResultPath IS NOT NULL AS isResultAcceptable,
            orders.status IS 'РЕЗУЛЬТАТ' OR orders.status IS 'ГОТОВО' AS isDownloadable
        FROM orders WHERE orders.userId = @id OR orders.userRespondedId = @id
    ", new {id});
    return new
    {
        profileData,
        orders
    };
});

app.MapGet("/profiles", async () =>
{
    return await Connection.QueryAsync<UserProfileData>("SELECT * FROM users");
});


app.MapGet("/orders", async () =>
{
    return await Connection.QueryAsync<OrderData>(@"
        SELECT orders.*, creator.login as userLogin, responded.login as userRespondedLogin  FROM orders 
            INNER JOIN users creator 
                ON creator.userId = orders.userId
            LEFT JOIN users responded 
                ON responded.userId = orders.userRespondedId");
});


app.MapPut("/order", async (string service, int cost, string dateRange, string title, int orderId) =>
{
    await Connection.ExecuteAsync(
        @"UPDATE orders 
                SET service = @service, cost = @cost, dateRange = @dateRange, title = @title 
                WHERE  orderId = @orderId", new { service, cost, dateRange, title, orderId });
});

app.MapDelete("/order", async (string orderId, bool isDeleted) =>
{
    await Connection.ExecuteAsync("UPDATE orders SET isDeleted = @isDeleted  WHERE orderId = @orderId",
        new {orderId, isDeleted = !isDeleted});
});

app.MapPost("/profile/avatar", async (int userId, string avatarPath) =>
{
    await Connection.ExecuteAsync("UPDATE users SET avatarPath = @avatarPath WHERE userId = @userId",
        new { userId, avatarPath });
});
app.MapPut("/profile", async ([FromBody]UpdateLogin updateLogin) =>
{
    await Connection.ExecuteAsync("UPDATE users SET login = @Login, portfolio = @Portfolio, status = @Status, service = @Service  WHERE userId = @UserId",
        updateLogin);
});

app.MapDelete("/profile", async (int userId, bool isDeleted) =>
{
    await Connection.ExecuteAsync("UPDATE users SET isDeleted = @isDeleted  WHERE userId = @userId",
        new {userId, isDeleted = !isDeleted});
});
app.MapPost("/login", async (LoginData data) =>
{
    var newData = data with
    {
        Password = HashPassword(data.Password)
    };
    var userId = await Connection.ExecuteScalarAsync<int>(
        "SELECT userId FROM users WHERE login = @Login AND passwordHash = @Password; " ,
        newData);
    if (userId == 0)
    {
        return Results.BadRequest(new
        {
            Message = "Пользователь не существует"
        });
    }
    return Results.Ok(new
    {
        Id = userId
    });
}).DisableAntiforgery();;


app.MapGet("/orders/{page:int}", async (int page, int userId) =>
{
    var orders = await Connection.QueryAsync<OrderResponse>(@$"
        SELECT title, service, description, photoPath, cost, orders.orderId, status, 
               from_user_orders.fromUserId IS NOT NULL AS userIsResponded, dateRange
        FROM orders
        LEFT JOIN users_orders_actions from_user_orders 
            ON from_user_orders.orderId = orders.orderId
            AND from_user_orders.fromUserId = @userId
        WHERE 
            orders.userId IS NOT @userId 
            AND orders.userRespondedId IS NOT @userId 
            AND orders.userId IS NOT @userId 
            AND status IS 'АКТИВЕН'
        LIMIT 2 OFFSET @offset;    
    ", new {userId, offset=(page - 1) * 2});
    return orders;
}).DisableAntiforgery();;

app.MapPost("/orders/response", async (int orderId, int userId) =>
{
    var (orderCreatorId, comment) = await Connection.QuerySingleAsync<(int, string)>(
        "SELECT userId, comment FROM orders WHERE orderId = @orderId " ,
    new {orderId});
    if (!string.IsNullOrWhiteSpace(comment))
    {
        await Connection.ExecuteAsync(
            "INSERT INTO users_orders_actions(orderId, creatorOrderId, fromUserId, message, actionDate, actionName, toUserId) " +
            "   VALUES (@orderId, @orderCreatorId, @orderCreatorId, @comment, @ActionDate, \"КОММЕНТАРИЙ\", @userId)", 
            new { orderId, userId, orderCreatorId, comment, ActionDate = DateTime.Now.ToString("dd.MM.yyyy hh:mm") });
    }
    await Connection.ExecuteAsync(
        "INSERT INTO users_orders_actions(orderId, creatorOrderId, fromUserId, message, actionDate, actionName, toUserId) " +
        "   VALUES (@orderId, @orderCreatorId, @userId, \"Откликнулся на заказ\", @ActionDate, \"ОТКЛИК\", @orderCreatorId)", 
        new { orderId, userId, orderCreatorId, ActionDate = DateTime.Now.ToString("dd.MM.yyyy hh:mm") });
});
app.MapGet("/messages/{page:int}", async (int userId, int page) =>
{
    return await Connection.QueryAsync<OrderUser>(
    $@"
        SELECT orders.title as orderTitle, fromUserId, toUserId, orderCreators.login as orderCreatorLogin, 
            toUsers.login as toUserLogin, message, actionName, actionDate, 
            users_orders_actions.fromUserId IS @userId AS isFromCurrentUser, 
            users_orders_actions.toUserId IS @userId AS isToCurrentUser, orders.orderId,
            CASE
                WHEN fromUserId IS @userId THEN 'ВЫ'
                WHEN fromUserId IS NOT @userId THEN fromUsers.login
            END AS fromUserLogin,
            CASE
                WHEN toUserId IS @userId THEN 'ВЫ'
                WHEN toUserId IS NOT @userId THEN toUsers.login
            END AS toUserLogin,
            users_orders_actions.fromUserId IS NOT @userId AND users_orders_actions.creatorOrderId IS @userId AND orders.userRespondedId IS NULL as isAcceptable,
            orders.fileResultPath,
            orders.fileResultPath IS NOT NULL AND actionName IS 'РЕЗУЛЬТАТ' as hasFileResult
        FROM users_orders_actions
        INNER JOIN users as orderCreators ON orderCreators.userId = creatorOrderId
        INNER JOIN users as fromUsers ON fromUsers.userId = fromUserId
        INNER JOIN users as toUsers ON toUsers.userId = toUserId
        INNER JOIN orders ON orders.orderId = users_orders_actions.orderId
        WHERE toUserId = @userId OR fromUserId = @userId or creatorOrderId = @userId
        ORDER BY actionDate DESC
        LIMIT 2 OFFSET {(page - 1) * 2}
    ", new { userId });
});

app.MapPost("/orders/accept", async (int userId, int orderId) =>
{
    var orderCreatorId = await Connection.ExecuteScalarAsync<int>(
        "SELECT userId FROM orders WHERE orderId = @orderId ", new { orderId});
    await Connection.ExecuteAsync(
        "INSERT INTO users_orders_actions(orderId, creatorOrderId, fromUserId, message, actionDate, actionName, toUserId) " +
        "   VALUES (@orderId, @orderCreatorId, @orderCreatorId, \"ПРИНЯЛ ПРЕДЛОЖЕНИЕ\", @ActionDate, \"ПРИНЯЛ\", @userId)", 
        new { orderId, userId, orderCreatorId, ActionDate = DateTime.Now.ToString("dd.MM.yyyy hh:mm") });
    await Connection.ExecuteAsync(
        "UPDATE orders SET userRespondedId = @userId, status = @status WHERE orderId = @orderId", 
        new {userId, orderId, status = "ВЗЯТ"});
});

app.MapGet("/professional-results", async () =>
{
    return await Connection.QueryAsync<ShowResult>(@"
        SELECT users.login, orders.fileResultPath, users.avatarPath 
            from orders INNER JOIN users 
                ON orders.userRespondedId = users.userId 
                       AND orders.status = 'ГОТОВО' 
                       AND (orders.fileResultPath LIKE '%.jpg' 
                       OR orders.fileResultPath LIKE '%.jpeg' 
                       OR orders.fileResultPath LIKE '%.png')
    ");
});

app.MapPost("/orders", async (OrderModel order) =>
{
    Connection.Open();
    
    

    var today = DateTime.Now.ToString("dd.MM.yyyy");
    var threeMonthsLater = (DateTime.Today + TimeSpan.FromDays(90)).ToString("dd.MM.yyyy");
    
    var orderId = await Connection.ExecuteScalarAsync($@"
         INSERT INTO orders (
            userId, description, service, cost, title, photoPath, file1Path, file2Path, file3Path, status, dateRange, comment
        ) VALUES (
            @UserId, @Description, @Service, @Cost, @Title, @PhotoPath, @File1Path, @File2Path, @File3Path, 'АКТИВЕН', ""{today} - {threeMonthsLater}"", @Comment
        );
        SELECT last_insert_rowid();", order);
    
    var desiredUserId = await Connection.ExecuteScalarAsync<int>(
        "SELECT userId FROM users WHERE login = @DesiredUserName " ,
        order);
    OrderModel newOrder;
    if (desiredUserId != 0)
    {
        await Connection.ExecuteAsync(
            "INSERT INTO users_orders_actions(orderId, creatorOrderId, fromUserId, message, actionDate, actionName, toUserId) " +
            "   VALUES (@orderId, @orderCreatorId, @orderCreatorId, @message, @ActionDate, \"ОТКЛИК\", @userId)", 
            new { orderId, userId = desiredUserId, message = order.CommentForDesiredUser, orderCreatorId = order.UserId, ActionDate = DateTime.Now.ToString("dd.MM.yyyy hh:mm") });
    }
    return Results.Ok(new { orderId });
    
    
    
    
    
}).DisableAntiforgery();;

app.MapPost("/files", async ([FromForm]IFormFile file) =>
{
    var filePath = Path.Combine("wwwroot\\files", file.FileName);

    // Создание папки, если она не существует
    Directory.CreateDirectory("UploadedFiles");

    await using var stream = new FileStream(filePath, FileMode.Create);
    await file.CopyToAsync(stream);

    return Results.Ok(new { FilePath = "files/" + file.FileName });
}).DisableAntiforgery();


app.Run();

public class OrderAcceptResultData
{
    public int UserRespondedId { get; set; }
    public int UserId { get; set; }
}

public class ShowResult
{
    public string Login { get; set; }
    public string FileResultPath { get; set; }
    public string AvatarPath { get; set; }
}

public record UpdateLogin(string Login, string Portfolio, string Status, string Service, int UserId);

public record UploadOrderResult(int UserId, int OrderId, string FilePath);

public class UserProfileData
{
    public int UserId { get; set; } 
    public string Login { get; set; }
    public string Status { get; set;  }
    public string PhoneNumber { get; set; } 
    public string PasswordHash { get; set; } 
    public string Service { get; set; } 
    public string Portfolio { get; set; } 
    public string AvatarPath { get; set; }
    public bool IsDeleted { get; set; }
}

public class OrderData
{
    public int OrderId { get; set; }
    public int UserId { get; set; }
    public string UserLogin { get; set; }
    public string UserRespondedLogin { get; set; }
    public int UserRespondedId { get; set; }
    public string Description { get; set; }
    public string Service { get; set; }
    public double Cost { get; set; }
    public string Comment { get; set; }
    public string DateRange { get; set; }
    public string Title { get; set; }
    public string PhotoPath { get; set; }
    public string FilePath { get; set; }
    public string File2Path { get; set; }
    public string File3Path { get; set; }
    public string FileResultPath { get; set; }
    public string Status { get; set; }
    public bool IsDeleted { get; set; }
}
public class OrderUser
{
    // orders.title as orderTitle, orderCreators.login as orderCreatorLogin, 
    // fromUsers.login as fromUserLogin, toUsers.login as toUserLogin, message, actionName, actionDate
    public string OrderTitle { get; set; }
    public string OrderCreatorLogin { get; set; }
    public string FromUserLogin { get; set; }
    public string ToUserLogin { get; set; }
    public string Message { get; set; }
    public string ActionName { get; set; }
    public string ActionDate { get; set; }
    public int FromUserId { get; set; }
    public int ToUserId { get; set; }
    public bool IsFromCurrentUser { get; set; }
    public bool IsToCurrentUser { get; set; }
    public int OrderId { get; set; }
    public bool IsAcceptable { get; set; }
    public string FileResultPath { get; set; }
    public bool HasFileResult { get; set; }
}

public class ProfileOrder
{
    public string Title { get; set; }
    public string Status { get; set; }
    public string DateRange { get; set; }
    public string UserAction { get; set; }
    public string Login { get; set; }
    public int OrderId { get; set; }
    public string FileResultPath { get; set; }
    public bool IsUploadable { get; set; }
    public bool IsResultAcceptable { get; set; }
    public bool IsDownloadable { get; set; }
}
public class OrderResponse
{
    public string Cost { get; set; }            // Цена заказа (например, "2500 Руб.")
    public string DateRange { get; set; }        // Диапазон дат (например, "27.11.2025 - 30.11")
    public string Service { get; set; } // Список категорий (например, ["WEB-DESIGN", "FRONTEND"])
    public string Status { get; set; }           // Статус заказа (например, "АКТИВНО")
    public string Description { get; set; }      // Описание заказа (например, "СУПЕР МЕГА ОПИСАНИЕ ПРОЕКТА КОТОРЫЙ ВЗЛЕТИТ")
    public string Title { get; set; }
    public string PhotoPath { get; set; }
    public int OrderId { get; set; }
    public bool UserIsResponded { get; set; }
}

class ProfileData
{
    public string Login { get; set; }
    public int Score { get; set; }
    public string Portfolio { get; set; }
    public string Status { get; set; } 
    public string Service { get; set; }
    public string AvatarPath { get; set; }
};

record LoginData(string Login, string Password);

record RegisterData(string Login, string Password, string Service, string PhoneNumber);

public record OrderModel(
    int UserId, // Обязательное поле
    string? DesiredUserName, // Необязательное поле
    int? DesiredUserId,
    string? CommentForDesiredUser, // Необязательное поле
    string Description,
    string Service,
    decimal Cost,
    string Comment,
    string Title,
    string PhotoPath,
    string File1Path,
    string File2Path,
    string File3Path,
    string Status
);
