using System;
using System.Threading.Tasks;
using Longhand.Web;

var options = WebOptions.FromArgs(args);
var app = WebApp.Build(options);
Console.WriteLine($"Longhand is at {options.Urls}");
Console.WriteLine($"Notes are kept in {options.DataDir}");
if (options.SignIn is null && Environment.GetEnvironmentVariable("LONGHAND_CODEX_SIGNIN") is null)
    Console.WriteLine("To get answers, paste your ChatGPT sign-in (~/.codex/auth.json) with the gear button in the page.");
await app.RunAsync();
