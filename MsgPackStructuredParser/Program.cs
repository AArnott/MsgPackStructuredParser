using System.Buffers;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

RootCommand rootCommand = new("Converts msgpack to a precise textual rendering.");
Option<string> inputArg = new(["--input", "-i"], "The msgpack data file to read in. If omitted, STDIN will be used. Piping to STDIN may not work well from a Windows shell.");
Option<string> outputArg = new(["--output", "-o"], "The file to write text to. If omitted, STDOUT will be used.");
Option<bool> includePositionsOption = new(["--include-positions"], "Include byte positions for each token.");
rootCommand.AddOption(inputArg);
rootCommand.AddOption(outputArg);
rootCommand.AddOption(includePositionsOption);
rootCommand.SetHandler(
    async ctxt =>
    {
        string? inputFile = ctxt.ParseResult.GetValueForOption(inputArg);
        string? outputFile = ctxt.ParseResult.GetValueForOption(outputArg);
        bool includePositions = ctxt.ParseResult.GetValueForOption(includePositionsOption);
        using Stream inputStream = inputFile is null ? Console.OpenStandardInput() : File.OpenRead(inputFile);
        using TextWriter outputWriter = outputFile is null ? Console.Out : new StreamWriter(File.Open(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read));
        Converter converter = new()
        {
            IncludePositions = includePositions,
        };
        await converter.ToTextWriterAsync(inputStream, outputWriter, ctxt.GetCancellationToken());
    });

await new CommandLineBuilder(rootCommand)
    .UseDefaults()
    .Build()
    .InvokeAsync(args);

class Converter
{
    public bool IncludePositions { get; init; }

    internal async Task ToTextWriterAsync(Stream input, TextWriter output, CancellationToken cancellationToken)
    {
        Sequence<byte> inputBuilder = new();
        byte[] buffer = new byte[1024];
        int bytesRead;
        do
        {
            bytesRead = await input.ReadAsync(buffer, cancellationToken);
            inputBuilder.Write(buffer.AsSpan(..bytesRead));
        }
        while (bytesRead > 0);

        ToTextWriter(inputBuilder, output, cancellationToken);
    }

    void ToTextWriter(ReadOnlySequence<byte> input, TextWriter output, CancellationToken cancellationToken)
    {
        MessagePackReader reader = new(input);
        while (!reader.End)
        {
            LogStruct(ref reader, 0);
        }

        void LogStruct(ref MessagePackReader reader, int level)
        {
            MessagePackReader before = reader;
            switch (reader.NextMessagePackType)
            {
                case MessagePackType.Unknown:
                    reader.Skip(new SerializationContext());
                    Log(before, reader, "unknown");
                    break;
                case MessagePackType.Integer:
                    if (IsSignedInteger(reader.NextCode))
                    {
                        Log(before, reader, reader.ReadInt64().ToString());
                    }
                    else
                    {
                        Log(before, reader, reader.ReadUInt64().ToString());
                    }

                    break;
                case MessagePackType.Nil:
                    reader.ReadNil();
                    Log(before, reader, "nil");
                    break;
                case MessagePackType.Boolean:
                    Log(before, reader, reader.ReadBoolean().ToString());
                    break;
                case MessagePackType.Float:
                    Log(before, reader, reader.ReadDouble().ToString());
                    break;
                case MessagePackType.String:
                    Log(before, reader, $"\"{reader.ReadString()}\"");
                    break;
                case MessagePackType.Binary:
                    Log(before, reader, Convert.ToHexString(reader.ReadBytes()!.Value.ToArray()));
                    break;
                case MessagePackType.Array:
                    int length = reader.ReadArrayHeader();
                    Log(before, reader, $"array({length})");
                    for (int i = 0; i < length; i++)
                    {
                        LogStruct(ref reader, level + 1);
                    }
                    break;
                case MessagePackType.Map:
                    length = reader.ReadMapHeader();
                    Log(before, reader, $"map({length})");
                    for (int i = 0; i < length; i++)
                    {
                        LogStruct(ref reader, level + 1); // key
                        LogStruct(ref reader, level + 1); // value
                    }
                    break;
                case MessagePackType.Extension:
                    ExtensionHeader header = reader.ReadExtensionHeader();
                    Log(before, reader, $"typecode={header.TypeCode}, length={header.Length}, {Convert.ToHexString(reader.ReadRaw(header.Length).ToArray())}");
                    break;
                default:
                    throw new NotSupportedException();
            }

            void Log(in MessagePackReader before, in MessagePackReader after, ReadOnlySpan<char> decoded)
            {
                LogSpan(before.Sequence.GetOffset(before.Position), after.Sequence.Slice(before.Position, after.Position).ToArray(), before.NextCode, before.NextMessagePackType, decoded);
            }

            void LogSpan(long position, ReadOnlySpan<byte> msgpack, byte typecode, MessagePackType type, ReadOnlySpan<char> decoded)
            {
                string indent = new string(' ', level * 2);
                // {Convert.ToHexString(msgpack)}
                string lineNumber = this.IncludePositions ? $"{position,6} " : string.Empty;
                string line = $"{lineNumber}{indent}[{MessagePackCode.ToFormatName(typecode)}] {decoded}";
                output.WriteLine(line);
            }
        }

        static bool IsSignedInteger(byte code)
        {
            switch (code)
            {
                case MessagePackCode.Int8:
                case MessagePackCode.Int16:
                case MessagePackCode.Int32:
                case MessagePackCode.Int64:
                    return true;
                default:
                    return code >= MessagePackCode.MinNegativeFixInt && code <= MessagePackCode.MaxNegativeFixInt;
            }
        }
    }
}