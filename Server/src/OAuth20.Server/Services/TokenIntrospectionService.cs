﻿/*
                        GNU GENERAL PUBLIC LICENSE
                          Version 3, 29 June 2007
 Copyright (C) 2022 Mohammed Ahmed Hussien babiker Free Software Foundation, Inc. <https://fsf.org/>
 Everyone is permitted to copy and distribute verbatim copies
 of this license document, but changing it is not allowed.
 */

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OAuth20.Server.Configuration;
using OAuth20.Server.Models;
using OAuth20.Server.Models.Context;
using OAuth20.Server.OauthRequest;
using OAuth20.Server.OauthResponse;
using OAuth20.Server.Validations;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace OAuth20.Server.Services
{
    public class TokenIntrospectionService : ITokenIntrospectionService
    {
        private readonly ITokenIntrospectionValidation _tokenIntrospectionValidation;
        private readonly ILogger<TokenIntrospectionService> _logger;
        private readonly BaseDBContext _dbContext;
        private readonly OAuthServerOptions _optionsMonitor;
        private readonly ClientStore _clientStore = new ClientStore();
        public TokenIntrospectionService(
            ITokenIntrospectionValidation tokenIntrospectionValidation,
            ILogger<TokenIntrospectionService> logger,
            BaseDBContext dbContext,
            IOptionsMonitor<OAuthServerOptions> optionsMonitor
            )
        {
            _tokenIntrospectionValidation = tokenIntrospectionValidation;
            _logger = logger;
            _dbContext = dbContext;
            _optionsMonitor = optionsMonitor.CurrentValue ?? new OAuthServerOptions();
        }

        public async Task<TokenIntrospectionResponse> IntrospectTokenAsync(TokenIntrospectionRequest tokenIntrospectionRequest)
        {
            TokenIntrospectionResponse response = new();
            var validationResult = await _tokenIntrospectionValidation.ValidateAsync(tokenIntrospectionRequest);
            if (validationResult.Succeeded == false)
                response.Active = false;

            else
            {
                RSACryptoServiceProvider provider = new RSACryptoServiceProvider();
                string publicPrivateKey = File.ReadAllText("PublicPrivateKey.xml");
                provider.FromXmlString(publicPrivateKey);

                RsaSecurityKey rsaSecurityKey = new RsaSecurityKey(provider);
                JwtSecurityTokenHandler jwtSecurityTokenHandler = new JwtSecurityTokenHandler();
                JwtSecurityToken jwtSecurityToken = jwtSecurityTokenHandler.ReadJwtToken(tokenIntrospectionRequest.Token);

                TokenValidationParameters tokenValidationParameters = new TokenValidationParameters();

                tokenValidationParameters.IssuerSigningKey = rsaSecurityKey;
                tokenValidationParameters.ValidAudiences = jwtSecurityToken.Audiences;
                tokenValidationParameters.ValidTypes = new[] { "JWT" };
                tokenValidationParameters.ValidateIssuer = true;
                tokenValidationParameters.ValidIssuer = _optionsMonitor.IDPUri;
                tokenValidationParameters.ValidateAudience = true;
                tokenValidationParameters.AudienceValidator = ValidateAudienceHandler(jwtSecurityToken.Audiences, jwtSecurityToken,
                    tokenValidationParameters, validationResult.Client, tokenIntrospectionRequest.Token);

                try
                {
                    var tokenValidationReslt = await jwtSecurityTokenHandler.ValidateTokenAsync(tokenIntrospectionRequest.Token, tokenValidationParameters);

                    if (tokenValidationReslt.IsValid)
                    {
                        //int exp = (int)jwtSecurityToken.ValidTo.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
                        int exp = (int)tokenValidationReslt.Claims.FirstOrDefault(x => x.Key == "exp").Value;
                        string scope = (tokenValidationReslt.Claims.FirstOrDefault(x => x.Key == "scope").Value).ToString();
                        string aud = (tokenValidationReslt.Claims.FirstOrDefault(x => x.Key == "aud").Value).ToString();

                        response.Active = true;
                        response.TokenType = "access_token";
                        response.Exp = exp;
                        response.Iat = (int)jwtSecurityToken.IssuedAt.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
                        response.Iss = _optionsMonitor.IDPUri;
                        response.Scope = scope;
                        response.Aud = aud;
                        response.Nbf = (int)jwtSecurityToken.IssuedAt.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogCritical("There is an exception that is thrown while validating the token {exception}", ex);
                    response.Active = false;
                }
            }

            return response;
        }

        private AudienceValidator ValidateAudienceHandler(IEnumerable<string> audiences, SecurityToken securityToken,
            TokenValidationParameters validationParameters, Client client, string token)
        {
            Func<IEnumerable<string>, SecurityToken, TokenValidationParameters, bool> handler = (audiences, securityToken, validationParameters) =>
            {
                // Check the Token the Back Store.
                var tokenInDb = _dbContext.OAuthTokens.FirstOrDefault(x => x.Token == token);
                if (tokenInDb == null)
                    return false;

                if (tokenInDb.Revoked)
                    return false;

                return true;
            };
            return new AudienceValidator(handler);
        }

        private IList<Claim> ParseClaims(JwtSecurityToken tokenContent)
        {
            var claims = tokenContent.Claims.ToList();

            // claims.Add(new Claim(ClaimTypes.Name, tokenContent.Actor));
            return claims;
        }
    }
}
