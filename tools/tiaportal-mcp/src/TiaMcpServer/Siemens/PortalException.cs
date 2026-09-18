#region

using System;
using System.Collections.Generic;

#endregion

namespace TiaMcpServer.Siemens;

public class PortalException : Exception
{
  public PortalException(PortalErrorCode code, string message, IEnumerable<string>? candidates = null,
    Exception? inner = null) : base(message, inner)
  {
    this.Code = code;
    this.Candidates = candidates;
  }

  public PortalErrorCode Code { get; }

  public IEnumerable<string>? Candidates { get; }
}
