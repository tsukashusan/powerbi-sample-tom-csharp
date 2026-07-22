Console.WriteLine("Hello, World!");

// create the connect string
string workspaceConnection = "powerbi://api.powerbi.com/v1.0/myorg/LearningTOM";
string connectString = $"DataSource={workspaceConnection};";
// connect to the Power BI workspace referenced in connect string
Server server = new Server();
server.Connect(connectString);
// enumerate through models in workspace to display their names
foreach (Database database in server.Databases) {
  Console.WriteLine(database.Name);
}