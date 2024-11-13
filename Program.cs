using Microsoft.Data.SqlClient;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var configurationService = app.Services.GetService<IConfiguration>();
string connectionString = configurationService["ConnectionStrings:DefaultConnection"];


app.Run(async (context) =>
{
    var response = context.Response;
    var request = context.Request;

    string path = request.Path.Value?.ToLower() ?? "/";

    string method = request.Method.ToUpper();

    if (path == "/" && method == "GET")
    {
        string? search = request.Query["search"];
        string? sort = request.Query["sort"];
        string? pageStr = request.Query["page"];
        int page = 1;
        int pageSize = 10;

        if (!string.IsNullOrWhiteSpace(pageStr) && int.TryParse(pageStr, out int parsedPage))
        {
            page = parsedPage > 0 ? parsedPage : 1;
        }

        List<User> users = new List<User>();
        int totalUsers = 0;

        try
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                StringBuilder queryBuilder = new StringBuilder("SELECT Id, Name, Age FROM Users");
                List<SqlParameter> parameters = new List<SqlParameter>();

                if (!string.IsNullOrWhiteSpace(search))
                {
                    queryBuilder.Append(" WHERE Name LIKE @Search");
                    parameters.Add(new SqlParameter("@Search", $"%{search}%"));
                }

                if (!string.IsNullOrWhiteSpace(sort))
                {
                    if (sort == "Name" || sort == "Age")
                    {
                        queryBuilder.Append($" ORDER BY {sort}");
                    }
                    else
                    {
                        queryBuilder.Append(" ORDER BY Id");
                    }
                }
                else
                {
                    queryBuilder.Append(" ORDER BY Id");
                }

                // ���������� ���������
                queryBuilder.Append(" OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY");
                parameters.Add(new SqlParameter("@Offset", (page - 1) * pageSize));
                parameters.Add(new SqlParameter("@PageSize", pageSize));

                string finalQuery = queryBuilder.ToString();

                SqlCommand command = new SqlCommand(finalQuery, connection);
                if (parameters.Any())
                {
                    command.Parameters.AddRange(parameters.ToArray());
                }

                using (SqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    if (reader.HasRows)
                    {
                        while (await reader.ReadAsync())
                        {
                            users.Add(new User(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
                        }
                    }
                }

                // ��������� ������ ���������� ������������� ��� ���������
                StringBuilder countQueryBuilder = new StringBuilder("SELECT COUNT(*) FROM Users");
                if (!string.IsNullOrWhiteSpace(search))
                {
                    countQueryBuilder.Append(" WHERE Name LIKE @Search");
                }
                SqlCommand countCommand = new SqlCommand(countQueryBuilder.ToString(), connection);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    countCommand.Parameters.AddWithValue("@Search", $"%{search}%");
                }
                totalUsers = (int)await countCommand.ExecuteScalarAsync();
            }
        }
        catch (SqlException ex)
        {
            response.StatusCode = 500;
            await response.WriteAsync($"������ ��� ����������� � ���� ������: {ex.Message}");
            return;
        }

        string tableHtml = BuildHtmlTable(users);

        int totalPages = (int)Math.Ceiling((double)totalUsers / pageSize);

        // ��������� ������� ���������
        string paginationHtml = GeneratePagination(page, totalPages, search, sort);

        string htmlContent = GenerateMainPageWithPagination(search, sort, tableHtml, paginationHtml);
        await response.WriteAsync(htmlContent);
    }
    else if (path == "/add" && method == "GET")
    {
        // ����������� ����� ���������� ������������
        response.ContentType = "text/html; charset=utf-8";
        await response.SendFileAsync("wwwroot/add.html");
    }
    else if (path == "/add" && method == "POST")
    {
        // ��������� ���������� ������������
        var form = await request.ReadFormAsync();
        string? name = form["Name"];
        string? ageString = form["Age"];

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ageString) || !int.TryParse(ageString, out int age))
        {
            response.StatusCode = 400; 
            await response.WriteAsync("������������ ������. ����������, ��������� ��� ���� ���������.");
            return;
        }

        try
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                string insertQuery = "INSERT INTO Users (Name, Age) VALUES (@Name, @Age)";
                using (SqlCommand command = new SqlCommand(insertQuery, connection))
                {
                    command.Parameters.AddWithValue("@Name", name);
                    command.Parameters.AddWithValue("@Age", age);
                    await command.ExecuteNonQueryAsync();
                }
            }
            response.Redirect("/");
        }
        catch (SqlException ex)
        {
            response.StatusCode = 500; 
            await response.WriteAsync($"������ ��� ���������� ������������: {ex.Message}");
        }
    }
    else if (path.StartsWith("/edit/") && method == "GET")
    {
        // ���������� ID ������������ �� ����
        string[] segments = path.Split('/');
        if (segments.Length != 3 || !int.TryParse(segments[2], out int id))
        {
            response.StatusCode = 400;
            await response.WriteAsync("������������ ������������� ������������.");
            return;
        }

        User? user = null;
        try
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                string selectQuery = "SELECT Id, Name, Age FROM Users WHERE Id = @Id";
                using (SqlCommand command = new SqlCommand(selectQuery, connection))
                {
                    command.Parameters.AddWithValue("@Id", id);
                    using (SqlDataReader reader = await command.ExecuteReaderAsync())
                    {
                        if (reader.HasRows)
                        {
                            if (await reader.ReadAsync())
                            {
                                user = new User(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2));
                            }
                        }
                    }
                }
            }

            if (user == null)
            {
                response.StatusCode = 404;
                await response.WriteAsync("������������ �� ������.");
                return;
            }
        }
        catch (SqlException ex)
        {
            response.StatusCode = 500;
            await response.WriteAsync($"������ ��� ��������� ������ ������������: {ex.Message}");
            return;
        }

        string editPage = GenerateEditPage(user);
        response.ContentType = "text/html; charset=utf-8";
        await response.WriteAsync(editPage);
    }
    else if (path.StartsWith("/edit/") && method == "POST")
    {
        string[] segments = path.Split('/');
        if (segments.Length != 3 || !int.TryParse(segments[2], out int id))
        {
            response.StatusCode = 400; 
            await response.WriteAsync("������������ ������������� ������������.");
            return;
        }

        var form = await request.ReadFormAsync();
        string? name = form["Name"];
        string? ageString = form["Age"];

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ageString) || !int.TryParse(ageString, out int age))
        {
            response.StatusCode = 400; 
            await response.WriteAsync("������������ ������. ����������, ��������� ��� ���� ���������.");
            return;
        }

        try
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                string updateQuery = "UPDATE Users SET Name = @Name, Age = @Age WHERE Id = @Id";
                using (SqlCommand command = new SqlCommand(updateQuery, connection))
                {
                    command.Parameters.AddWithValue("@Name", name);
                    command.Parameters.AddWithValue("@Age", age);
                    command.Parameters.AddWithValue("@Id", id);
                    int rowsAffected = await command.ExecuteNonQueryAsync();
                    if (rowsAffected == 0)
                    {
                        response.StatusCode = 404; 
                        await response.WriteAsync("������������ �� ������.");
                        return;
                    }
                }
            }
            response.Redirect("/");
        }
        catch (SqlException ex)
        {
            response.StatusCode = 500; 
            await response.WriteAsync($"������ ��� �������������� ������������: {ex.Message}");
        }
    }
    else if (path.StartsWith("/delete/") && method == "POST")
    {
        string[] segments = path.Split('/');
        if (segments.Length != 3 || !int.TryParse(segments[2], out int id))
        {
            response.StatusCode = 400;
            await response.WriteAsync("������������ ������������� ������������.");
            return;
        }

        try
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                string deleteQuery = "DELETE FROM Users WHERE Id = @Id";
                using (SqlCommand command = new SqlCommand(deleteQuery, connection))
                {
                    command.Parameters.AddWithValue("@Id", id);
                    int rowsAffected = await command.ExecuteNonQueryAsync();
                    if (rowsAffected == 0)
                    {
                        response.StatusCode = 404;
                        await response.WriteAsync("������������ �� ������.");
                        return;
                    }
                }
            }
            response.Redirect("/");
        }
        catch (SqlException ex)
        {
            response.StatusCode = 500; 
            await response.WriteAsync($"������ ��� �������� ������������: {ex.Message}");
        }
    }
    else
    {
        response.StatusCode = 404;
        await response.WriteAsJsonAsync("Page Not Found");
    }
});

app.Run();

// ����� ��� ��������� ������� �������� � ����������
static string GenerateMainPageWithPagination(string? search, string? sort, string table, string pagination)
{
    string sortNameSelected = sort == "Name" ? "selected" : "";
    string sortAgeSelected = sort == "Age" ? "selected" : "";

    string html = $"""
        <!DOCTYPE html>
        <html lang="ru">
        <head>
            <meta charset="utf-8" />
            <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0-alpha3/dist/css/bootstrap.min.css" rel="stylesheet" 
            integrity="sha384-KK94CHFLLe+nY2dmCWGMq91rCGa5gtU4mk92HdvYe+M/SXH301p5ILy+dN9+nJOZ" crossorigin="anonymous">
            <title>��� ������������</title>
        </head>
        <body>
            <div class="container mt-5">
                <h2 class="text-center">��� ������������ �� ���� ������</h2>
                <div class="d-flex justify-content-between mt-4">
                    <form class="d-flex" action="/" method="get">
                        <input class="form-control me-2" type="search" placeholder="����� �� �����" aria-label="Search" name="search" value="{search}">
                        <button class="btn btn-outline-success" type="submit">�����</button>
                    </form>
                    <div>
                        <a href="/add" class="btn btn-primary">�������� ������������</a>
                    </div>
                </div>
                <div class="mt-3">
                    <form class="d-flex" action="/" method="get">
                        <label for="sort" class="me-2">����������� ��:</label>
                        <select class="form-select me-2" id="sort" name="sort">
                            <option value="">��� ����������</option>
                            <option value="Name" {sortNameSelected}>���</option>
                            <option value="Age" {sortAgeSelected}>�������</option>
                        </select>
                        <button class="btn btn-outline-primary" type="submit">�����������</button>
                    </form>
                </div>
                <div class="mt-4">
                    {table}
                </div>
                <nav aria-label="Page navigation example">
                    {pagination}
                </nav>
            </div>
            <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0-alpha3/dist/js/bootstrap.bundle.min.js" 
            integrity="sha384-ENjdO4Dr2bkBIFxQpeoTz1HIcje39Wm4jDKdf19U8gI4ddQ3GYNS7NTKfAdVQSZe" crossorigin="anonymous"></script>
        </body>
        </html>
        """;
    return html;
}

// ����� ��� ��������� ���������
static string GeneratePagination(int currentPage, int totalPages, string? search, string? sort)
{
    if (totalPages <= 1)
    {
        return string.Empty; // ��� ������������� � ���������
    }

    StringBuilder paginationBuilder = new StringBuilder();
    paginationBuilder.Append("<ul class=\"pagination justify-content-center\">");

    // ������ "����������"
    if (currentPage > 1)
    {
        paginationBuilder.Append($"""
            <li class="page-item">
                <a class="page-link" href="/?search={Uri.EscapeDataString(search ?? "")}&sort={Uri.EscapeDataString(sort ?? "")}&page={currentPage - 1}" aria-label="Previous">
                    <span aria-hidden="true">&laquo;</span>
                </a>
            </li>
            """);
    }
    else
    {
        paginationBuilder.Append($"""
            <li class="page-item disabled">
                <span class="page-link" aria-hidden="true">&laquo;</span>
            </li>
            """);
    }

    // ����������� ������� �������
    for (int i = 1; i <= totalPages; i++)
    {
        if (i == currentPage)
        {
            paginationBuilder.Append($"""
                <li class="page-item active" aria-current="page">
                    <span class="page-link">{i}</span>
                </li>
                """);
        }
        else
        {
            paginationBuilder.Append($"""
                <li class="page-item"><a class="page-link" href="/?search={Uri.EscapeDataString(search ?? "")}&sort={Uri.EscapeDataString(sort ?? "")}&page={i}">{i}</a></li>
                """);
        }
    }

    // ������ "���������"
    if (currentPage < totalPages)
    {
        paginationBuilder.Append($"""
            <li class="page-item">
                <a class="page-link" href="/?search={Uri.EscapeDataString(search ?? "")}&sort={Uri.EscapeDataString(sort ?? "")}&page={currentPage + 1}" aria-label="Next">
                    <span aria-hidden="true">&raquo;</span>
                </a>
            </li>
            """);
    }
    else
    {
        paginationBuilder.Append($"""
            <li class="page-item disabled">
                <span class="page-link" aria-hidden="true">&raquo;</span>
            </li>
            """);
    }

    paginationBuilder.Append("</ul>");

    return paginationBuilder.ToString();
}

static string BuildHtmlTable<T>(IEnumerable<T> collection)
{
    StringBuilder tableHtml = new StringBuilder();
    tableHtml.Append("<table class=\"table table-striped\">");

    PropertyInfo[] properties = typeof(T).GetProperties();

    tableHtml.Append("<thead><tr>");
    foreach (PropertyInfo property in properties)
    {
        tableHtml.Append($"<th>{property.Name}</th>");
    }
    tableHtml.Append("<th>��������</th>");
    tableHtml.Append("</tr></thead><tbody>");

    foreach (T item in collection)
    {
        tableHtml.Append("<tr>");
        foreach (PropertyInfo property in properties)
        {
            object value = property.GetValue(item);
            tableHtml.Append($"<td>{value}</td>");
        }
        // ���������� ������ "�������������" � "�������"
        int userId = (int)typeof(T).GetProperty("Id").GetValue(item)!;
        tableHtml.Append($"""
            <td>
                <a href="/edit/{userId}" class="btn btn-warning btn-sm">�������������</a>
                <form action="/delete/{userId}" method="post" style="display:inline;">
                    <button type="submit" class="btn btn-danger btn-sm" onclick="return confirm('�� �������, ��� ������ ������� ����� ������������?');">�������</button>
                </form>
            </td>
            """);
        tableHtml.Append("</tr>");
    }

    tableHtml.Append("</tbody></table>");
    return tableHtml.ToString();
}

static string GenerateEditPage(User user)
{
    string html = $"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8" />
            <title>������������� ������������</title>
            <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0-alpha3/dist/css/bootstrap.min.css" rel="stylesheet" 
            integrity="sha384-KK94CHFLLe+nY2dmCWGMq91rCGa5gtU4mk92HdvYe+M/SXH301p5ILy+dN9+nJOZ" crossorigin="anonymous">
        </head>
        <body>
            <div class="container mt-5">
                <h2 class="text-center">������������� ������������</h2>
                <form action="/edit/{user.Id}" method="post">
                    <div class="mb-3">
                        <label for="Name" class="form-label">���</label>
                        <input type="text" class="form-control" id="Name" name="Name" value="{user.Name}" required>
                    </div>
                    <div class="mb-3">
                        <label for="Age" class="form-label">�������</label>
                        <input type="number" class="form-control" id="Age" name="Age" value="{user.Age}" required>
                    </div>
                    <button type="submit" class="btn btn-primary">���������</button>
                    <a href="/" class="btn btn-secondary">������</a>
                </form>
            </div>
            <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0-alpha3/dist/js/bootstrap.bundle.min.js" 
            integrity="sha384-ENjdO4Dr2bkBIFxQpeoTz1HIcje39Wm4jDKdf19U8gI4ddQ3GYNS7NTKfAdVQSZe" crossorigin="anonymous"></script>
        </body>
        </html>
        """;
    return html;
}

// ������ ��� ������������
record User(int Id, string Name, int Age)
{
    public User(string Name, int Age) : this(0, Name, Age)
    {

    }

}
