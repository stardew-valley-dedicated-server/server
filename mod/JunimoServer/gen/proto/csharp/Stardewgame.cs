// <auto-generated>
//     Generated by the protocol buffer compiler.  DO NOT EDIT!
//     source: junimohost/stardewgame/v1/stardewgame.proto
// </auto-generated>
#pragma warning disable 1591, 0612, 3021, 8981
#region Designer generated code

using pb = global::Google.Protobuf;
using pbc = global::Google.Protobuf.Collections;
using pbr = global::Google.Protobuf.Reflection;
using scg = global::System.Collections.Generic;
namespace Junimohost.Stardewgame.V1 {

  /// <summary>Holder for reflection information generated from junimohost/stardewgame/v1/stardewgame.proto</summary>
  public static partial class StardewgameReflection {

    #region Descriptor
    /// <summary>File descriptor for junimohost/stardewgame/v1/stardewgame.proto</summary>
    public static pbr::FileDescriptor Descriptor {
      get { return descriptor; }
    }
    private static pbr::FileDescriptor descriptor;

    static StardewgameReflection() {
      byte[] descriptorData = global::System.Convert.FromBase64String(
          string.Concat(
            "CitqdW5pbW9ob3N0L3N0YXJkZXdnYW1lL3YxL3N0YXJkZXdnYW1lLnByb3Rv",
            "EhlqdW5pbW9ob3N0LnN0YXJkZXdnYW1lLnYxIpUBChZHYW1lSGVhbHRoQ2hl",
            "Y2tSZXF1ZXN0EhsKCXNlcnZlcl9pZBgBIAEoCVIIc2VydmVySWQSKgoOaXNf",
            "Y29ubmVjdGFibGUYAiABKAhIAFINaXNDb25uZWN0YWJsZYgBARIfCgtpbnZp",
            "dGVfY29kZRgDIAEoCVIKaW52aXRlQ29kZUIRCg9faXNfY29ubmVjdGFibGUi",
            "GQoXR2FtZUhlYWx0aENoZWNrUmVzcG9uc2UyjgEKElN0YXJkZXdHYW1lU2Vy",
            "dmljZRJ4Cg9HYW1lSGVhbHRoQ2hlY2sSMS5qdW5pbW9ob3N0LnN0YXJkZXdn",
            "YW1lLnYxLkdhbWVIZWFsdGhDaGVja1JlcXVlc3QaMi5qdW5pbW9ob3N0LnN0",
            "YXJkZXdnYW1lLnYxLkdhbWVIZWFsdGhDaGVja1Jlc3BvbnNlQrcBCh1jb20u",
            "anVuaW1vaG9zdC5zdGFyZGV3Z2FtZS52MUIQU3RhcmRld2dhbWVQcm90b1AB",
            "ogIDSlNYqgIZSnVuaW1vaG9zdC5TdGFyZGV3Z2FtZS5WMcoCGUp1bmltb2hv",
            "c3RcU3RhcmRld2dhbWVcVjHiAiVKdW5pbW9ob3N0XFN0YXJkZXdnYW1lXFYx",
            "XEdQQk1ldGFkYXRh6gIbSnVuaW1vaG9zdDo6U3RhcmRld2dhbWU6OlYxYgZw",
            "cm90bzM="));
      descriptor = pbr::FileDescriptor.FromGeneratedCode(descriptorData,
          new pbr::FileDescriptor[] { },
          new pbr::GeneratedClrTypeInfo(null, null, new pbr::GeneratedClrTypeInfo[] {
            new pbr::GeneratedClrTypeInfo(typeof(global::Junimohost.Stardewgame.V1.GameHealthCheckRequest), global::Junimohost.Stardewgame.V1.GameHealthCheckRequest.Parser, new[]{ "ServerId", "IsConnectable", "InviteCode" }, new[]{ "IsConnectable" }, null, null, null),
            new pbr::GeneratedClrTypeInfo(typeof(global::Junimohost.Stardewgame.V1.GameHealthCheckResponse), global::Junimohost.Stardewgame.V1.GameHealthCheckResponse.Parser, null, null, null, null, null)
          }));
    }
    #endregion

  }
  #region Messages
  public sealed partial class GameHealthCheckRequest : pb::IMessage<GameHealthCheckRequest>
  #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      , pb::IBufferMessage
  #endif
  {
    private static readonly pb::MessageParser<GameHealthCheckRequest> _parser = new pb::MessageParser<GameHealthCheckRequest>(() => new GameHealthCheckRequest());
    private pb::UnknownFieldSet _unknownFields;
    private int _hasBits0;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pb::MessageParser<GameHealthCheckRequest> Parser { get { return _parser; } }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pbr::MessageDescriptor Descriptor {
      get { return global::Junimohost.Stardewgame.V1.StardewgameReflection.Descriptor.MessageTypes[0]; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    pbr::MessageDescriptor pb::IMessage.Descriptor {
      get { return Descriptor; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public GameHealthCheckRequest() {
      OnConstruction();
    }

    partial void OnConstruction();

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public GameHealthCheckRequest(GameHealthCheckRequest other) : this() {
      _hasBits0 = other._hasBits0;
      serverId_ = other.serverId_;
      isConnectable_ = other.isConnectable_;
      inviteCode_ = other.inviteCode_;
      _unknownFields = pb::UnknownFieldSet.Clone(other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public GameHealthCheckRequest Clone() {
      return new GameHealthCheckRequest(this);
    }

    /// <summary>Field number for the "server_id" field.</summary>
    public const int ServerIdFieldNumber = 1;
    private string serverId_ = "";
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public string ServerId {
      get { return serverId_; }
      set {
        serverId_ = pb::ProtoPreconditions.CheckNotNull(value, "value");
      }
    }

    /// <summary>Field number for the "is_connectable" field.</summary>
    public const int IsConnectableFieldNumber = 2;
    private bool isConnectable_;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool IsConnectable {
      get { if ((_hasBits0 & 1) != 0) { return isConnectable_; } else { return false; } }
      set {
        _hasBits0 |= 1;
        isConnectable_ = value;
      }
    }
    /// <summary>Gets whether the "is_connectable" field is set</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool HasIsConnectable {
      get { return (_hasBits0 & 1) != 0; }
    }
    /// <summary>Clears the value of the "is_connectable" field</summary>
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void ClearIsConnectable() {
      _hasBits0 &= ~1;
    }

    /// <summary>Field number for the "invite_code" field.</summary>
    public const int InviteCodeFieldNumber = 3;
    private string inviteCode_ = "";
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public string InviteCode {
      get { return inviteCode_; }
      set {
        inviteCode_ = pb::ProtoPreconditions.CheckNotNull(value, "value");
      }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override bool Equals(object other) {
      return Equals(other as GameHealthCheckRequest);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool Equals(GameHealthCheckRequest other) {
      if (ReferenceEquals(other, null)) {
        return false;
      }
      if (ReferenceEquals(other, this)) {
        return true;
      }
      if (ServerId != other.ServerId) return false;
      if (IsConnectable != other.IsConnectable) return false;
      if (InviteCode != other.InviteCode) return false;
      return Equals(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override int GetHashCode() {
      int hash = 1;
      if (ServerId.Length != 0) hash ^= ServerId.GetHashCode();
      if (HasIsConnectable) hash ^= IsConnectable.GetHashCode();
      if (InviteCode.Length != 0) hash ^= InviteCode.GetHashCode();
      if (_unknownFields != null) {
        hash ^= _unknownFields.GetHashCode();
      }
      return hash;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override string ToString() {
      return pb::JsonFormatter.ToDiagnosticString(this);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void WriteTo(pb::CodedOutputStream output) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      output.WriteRawMessage(this);
    #else
      if (ServerId.Length != 0) {
        output.WriteRawTag(10);
        output.WriteString(ServerId);
      }
      if (HasIsConnectable) {
        output.WriteRawTag(16);
        output.WriteBool(IsConnectable);
      }
      if (InviteCode.Length != 0) {
        output.WriteRawTag(26);
        output.WriteString(InviteCode);
      }
      if (_unknownFields != null) {
        _unknownFields.WriteTo(output);
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalWriteTo(ref pb::WriteContext output) {
      if (ServerId.Length != 0) {
        output.WriteRawTag(10);
        output.WriteString(ServerId);
      }
      if (HasIsConnectable) {
        output.WriteRawTag(16);
        output.WriteBool(IsConnectable);
      }
      if (InviteCode.Length != 0) {
        output.WriteRawTag(26);
        output.WriteString(InviteCode);
      }
      if (_unknownFields != null) {
        _unknownFields.WriteTo(ref output);
      }
    }
    #endif

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public int CalculateSize() {
      int size = 0;
      if (ServerId.Length != 0) {
        size += 1 + pb::CodedOutputStream.ComputeStringSize(ServerId);
      }
      if (HasIsConnectable) {
        size += 1 + 1;
      }
      if (InviteCode.Length != 0) {
        size += 1 + pb::CodedOutputStream.ComputeStringSize(InviteCode);
      }
      if (_unknownFields != null) {
        size += _unknownFields.CalculateSize();
      }
      return size;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(GameHealthCheckRequest other) {
      if (other == null) {
        return;
      }
      if (other.ServerId.Length != 0) {
        ServerId = other.ServerId;
      }
      if (other.HasIsConnectable) {
        IsConnectable = other.IsConnectable;
      }
      if (other.InviteCode.Length != 0) {
        InviteCode = other.InviteCode;
      }
      _unknownFields = pb::UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(pb::CodedInputStream input) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      input.ReadRawMessage(this);
    #else
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
        switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, input);
            break;
          case 10: {
            ServerId = input.ReadString();
            break;
          }
          case 16: {
            IsConnectable = input.ReadBool();
            break;
          }
          case 26: {
            InviteCode = input.ReadString();
            break;
          }
        }
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalMergeFrom(ref pb::ParseContext input) {
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
        switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, ref input);
            break;
          case 10: {
            ServerId = input.ReadString();
            break;
          }
          case 16: {
            IsConnectable = input.ReadBool();
            break;
          }
          case 26: {
            InviteCode = input.ReadString();
            break;
          }
        }
      }
    }
    #endif

  }

  public sealed partial class GameHealthCheckResponse : pb::IMessage<GameHealthCheckResponse>
  #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      , pb::IBufferMessage
  #endif
  {
    private static readonly pb::MessageParser<GameHealthCheckResponse> _parser = new pb::MessageParser<GameHealthCheckResponse>(() => new GameHealthCheckResponse());
    private pb::UnknownFieldSet _unknownFields;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pb::MessageParser<GameHealthCheckResponse> Parser { get { return _parser; } }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pbr::MessageDescriptor Descriptor {
      get { return global::Junimohost.Stardewgame.V1.StardewgameReflection.Descriptor.MessageTypes[1]; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    pbr::MessageDescriptor pb::IMessage.Descriptor {
      get { return Descriptor; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public GameHealthCheckResponse() {
      OnConstruction();
    }

    partial void OnConstruction();

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public GameHealthCheckResponse(GameHealthCheckResponse other) : this() {
      _unknownFields = pb::UnknownFieldSet.Clone(other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public GameHealthCheckResponse Clone() {
      return new GameHealthCheckResponse(this);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override bool Equals(object other) {
      return Equals(other as GameHealthCheckResponse);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool Equals(GameHealthCheckResponse other) {
      if (ReferenceEquals(other, null)) {
        return false;
      }
      if (ReferenceEquals(other, this)) {
        return true;
      }
      return Equals(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override int GetHashCode() {
      int hash = 1;
      if (_unknownFields != null) {
        hash ^= _unknownFields.GetHashCode();
      }
      return hash;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override string ToString() {
      return pb::JsonFormatter.ToDiagnosticString(this);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void WriteTo(pb::CodedOutputStream output) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      output.WriteRawMessage(this);
    #else
      if (_unknownFields != null) {
        _unknownFields.WriteTo(output);
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalWriteTo(ref pb::WriteContext output) {
      if (_unknownFields != null) {
        _unknownFields.WriteTo(ref output);
      }
    }
    #endif

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public int CalculateSize() {
      int size = 0;
      if (_unknownFields != null) {
        size += _unknownFields.CalculateSize();
      }
      return size;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(GameHealthCheckResponse other) {
      if (other == null) {
        return;
      }
      _unknownFields = pb::UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(pb::CodedInputStream input) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      input.ReadRawMessage(this);
    #else
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
        switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, input);
            break;
        }
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalMergeFrom(ref pb::ParseContext input) {
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
        switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, ref input);
            break;
        }
      }
    }
    #endif

  }

  #endregion

}

#endregion Designer generated code
