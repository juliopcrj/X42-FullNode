﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using NBitcoin.RPC;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stratis.Bitcoin.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NBitcoin.DataEncoders;

namespace Stratis.Bitcoin.RPC
{
	public class RPCMiddleware
	{
		RequestDelegate next;
		RPCAuthorization authorization;
		public RPCMiddleware(RequestDelegate next, RPCAuthorization authorization)
		{
			this.next = next;
			this.authorization = authorization;
		}
		public async Task Invoke(HttpContext httpContext)
		{
			if(!Authorized(httpContext))
			{
				httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}
			Exception ex = null;
			try
			{
				await next.Invoke(httpContext);
			}
			catch(Exception exx)
			{
				ex = exx;
			}
			if(httpContext.Response?.StatusCode == 404)
			{
				JObject response = CreateError(RPCErrorCode.RPC_METHOD_NOT_FOUND, "Method not found");
				await httpContext.Response.WriteAsync(response.ToString(Formatting.Indented));
			}
			if(httpContext.Response?.StatusCode == 500 || ex != null)
			{
				JObject response = CreateError(RPCErrorCode.RPC_INTERNAL_ERROR, "Internal error");
				Logs.RPC.LogError(new EventId(0), ex, "Internal error while calling RPC Method");
				await httpContext.Response.WriteAsync(response.ToString(Formatting.Indented));
			}
		}

		private bool Authorized(HttpContext httpContext)
		{
			StringValues auth;
			if(!httpContext.Request.Headers.TryGetValue("Authorization", out auth) || auth.Count != 1)
				return false;
			var splittedAuth = auth[0].Split(' ');
			if(splittedAuth.Length != 2 ||
			   splittedAuth[0] != "Basic")
				return false;

			try
			{
				var user = Encoders.ASCII.EncodeData(Encoders.Base64.DecodeData(splittedAuth[1]));
				if(!authorization.IsAuthorized(user))
					return false;
			}
			catch
			{
				return false;
			}
			return true;
		}

		private static JObject CreateError(RPCErrorCode code, string message)
		{
			JObject response = new JObject();
			response.Add("resut", null);
			JObject error = new JObject();
			response.Add("error", error);
			error.Add("code", (int)code);
			error.Add("message", message);
			return response;
		}
	}
}
