#region

using System;
using System.Collections.Generic;

#endregion

namespace TiaMcpServer.Siemens;

public class PortalException(
  PortalErrorCode code,
  string message,
  IEnumerable<string>? candidates = null,
  Exception? inner = null
) : Exception(message, inner)
{
  public PortalErrorCode Code { get; } = code;

  public IEnumerable<string>? Candidates { get; } = candidates;
}
