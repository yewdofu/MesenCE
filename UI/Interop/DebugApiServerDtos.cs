using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Mesen.Interop
{
	//JSON-RPC envelope types
	public record RpcRequest
	{
		[JsonPropertyName("jsonrpc")] public string? JsonRpc { get; init; }
		public JsonElement? Id { get; init; }
		public string? Method { get; init; }
		public JsonElement? Params { get; init; }
	}

	public record RpcResponse
	{
		[JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
		public JsonElement? Id { get; init; }
		public JsonElement? Result { get; init; }
		public RpcError? Error { get; init; }
	}

	public record RpcError
	{
		public int Code { get; init; }
		public string Message { get; init; } = "";
	}

	public record RpcNotification
	{
		[JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
		public string Method { get; init; } = "";
		public JsonElement? Params { get; init; }
	}

	//Notification payloads
	public record BreakNotificationData
	{
		public string BreakType { get; init; } = "";
		public string Cpu { get; init; } = "";
		public long Pc { get; init; }
		public long? BreakpointId { get; init; }
	}

	public record ResumedNotificationData
	{
		public string Cpu { get; init; } = "";
	}

	public record GameLoadedNotificationData
	{
		public string RomName { get; init; } = "";
		public string ConsoleType { get; init; } = "";
	}

	public record EmulationStoppedNotificationData
	{
	}

	//Method payloads
	public record SystemStatusResponse
	{
		public bool RomLoaded { get; init; }
		public string Console { get; init; } = "";
		public bool Running { get; init; }
		public bool Paused { get; init; }
	}

	public record InstructionResponse
	{
		public long Pc { get; init; }
		public long Address { get; init; }
		public string Text { get; init; } = "";
		public string ByteCode { get; init; } = "";
	}

	public record SnesRegistersResponse
	{
		public int A { get; init; }
		public int X { get; init; }
		public int Y { get; init; }
		public int Sp { get; init; }
		public int D { get; init; }
		public int Pc { get; init; }
		public int K { get; init; }
		public int Dbr { get; init; }
		public int Ps { get; init; }
		public bool EmulationMode { get; init; }
	}

	public record SnesRegistersUpdate
	{
		public int? A { get; init; }
		public int? X { get; init; }
		public int? Y { get; init; }
		public int? Sp { get; init; }
		public int? D { get; init; }
		public int? Pc { get; init; }
		public int? K { get; init; }
		public int? Dbr { get; init; }
		public int? Ps { get; init; }
		public bool? EmulationMode { get; init; }
	}

	public record MemoryRegionInfo
	{
		public string Id { get; init; } = "";
		public string Name { get; init; } = "";
		public long Size { get; init; }
	}

	public record MemoryListResponse
	{
		public List<MemoryRegionInfo> Regions { get; init; } = new();
	}

	public record MemoryReadRequest
	{
		public string Type { get; init; } = "";
		public long Address { get; init; }
		public int Length { get; init; }
	}

	public record MemoryReadResponse
	{
		public long Address { get; init; }
		public string Data { get; init; } = "";
	}

	public record MemoryWriteRequest
	{
		public string Type { get; init; } = "";
		public long Address { get; init; }
		public string Data { get; init; } = "";
	}

	public record MemoryWriteResponse
	{
		public int Written { get; init; }
	}

	public record BreakpointAddRequest
	{
		public string? Type { get; init; }
		public string? MemoryType { get; init; }
		public long Address { get; init; }
		public long? EndAddress { get; init; }
		public bool? Enabled { get; init; }
		public string? Condition { get; init; }
	}

	public record BreakpointRemoveRequest
	{
		public long Id { get; init; }
	}

	public record BreakpointInfo
	{
		public long Id { get; init; }
		public string Cpu { get; init; } = "";
		public string Type { get; init; } = "";
		public string MemoryType { get; init; } = "";
		public long Address { get; init; }
		public long? EndAddress { get; init; }
		public bool Enabled { get; init; }
	}

	public record BreakpointListResponse
	{
		public List<BreakpointInfo> Breakpoints { get; init; } = new();
	}

	[JsonSourceGenerationOptions(
		GenerationMode = JsonSourceGenerationMode.Metadata,
		PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	)]
	[JsonSerializable(typeof(RpcRequest))]
	[JsonSerializable(typeof(RpcResponse))]
	[JsonSerializable(typeof(RpcError))]
	[JsonSerializable(typeof(RpcNotification))]
	[JsonSerializable(typeof(BreakNotificationData))]
	[JsonSerializable(typeof(ResumedNotificationData))]
	[JsonSerializable(typeof(GameLoadedNotificationData))]
	[JsonSerializable(typeof(EmulationStoppedNotificationData))]
	[JsonSerializable(typeof(SystemStatusResponse))]
	[JsonSerializable(typeof(InstructionResponse))]
	[JsonSerializable(typeof(SnesRegistersResponse))]
	[JsonSerializable(typeof(SnesRegistersUpdate))]
	[JsonSerializable(typeof(MemoryRegionInfo))]
	[JsonSerializable(typeof(MemoryListResponse))]
	[JsonSerializable(typeof(MemoryReadRequest))]
	[JsonSerializable(typeof(MemoryReadResponse))]
	[JsonSerializable(typeof(MemoryWriteRequest))]
	[JsonSerializable(typeof(MemoryWriteResponse))]
	[JsonSerializable(typeof(BreakpointAddRequest))]
	[JsonSerializable(typeof(BreakpointRemoveRequest))]
	[JsonSerializable(typeof(BreakpointInfo))]
	[JsonSerializable(typeof(BreakpointListResponse))]
	[JsonSerializable(typeof(bool))]
	internal partial class DebugApiJsonContext : JsonSerializerContext
	{
	}
}
