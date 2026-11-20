using System.Text;
using CadAgent.Cli;

Console.OutputEncoding = Encoding.UTF8;
return CommandRouter.Run(args, Console.Out, Console.Error);