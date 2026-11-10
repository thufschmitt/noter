using System;
using System.Threading.Tasks;
using Longhand.Web;

var options = WebOptions.FromArgs(args);
var app = WebApp.Build(options);
Console.WriteLine($"Longhand is at {options.Urls}");
Console.WriteLine($"Notes are kept in {options.DataDir}");
if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is null)
    Console.WriteLine("No ANTHROPIC_API_KEY set: add a key with the gear button in the page.");
await app.RunAsync();
