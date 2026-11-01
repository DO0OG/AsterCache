using System.Text;
using AsterCache.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
return AsterCli.Run(args, Console.Out, Console.Error);
