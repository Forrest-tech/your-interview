#!/usr/bin/env python3
"""为所有微服务项目生成规范化的 .csproj(统一包引用 + 项目引用)。"""
import os, glob

SRC = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

SERVICE_PKGS = """    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
    <PackageReference Include="Swashbuckle.AspNetCore" />
    <PackageReference Include="MediatR" />
    <PackageReference Include="FluentValidation" />
    <PackageReference Include="FluentValidation.DependencyInjectionExtensions" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" />
    <PackageReference Include="Serilog.AspNetCore" />
    <PackageReference Include="MassTransit" />
    <PackageReference Include="MassTransit.RabbitMQ" />
    <PackageReference Include="MassTransit.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.Extensions.Http.Polly" />
    <PackageReference Include="Polly" />
    <PackageReference Include="Polly.Extensions" />"""

SERVICES = {
    "Services.Identity": None,
    "Services.Jobs": None,
    "Services.Interviews": None,
    "Services.Knowledge": None,
    "Services.Assessment": None,
    "Services.Analytics": None,
    "Analysis.Worker": None,
}

def refs(d):
    r = ['    <ProjectReference Include="..\\BuildingBlocks\\YourInterview.BuildingBlocks.csproj" />',
         '    <ProjectReference Include="..\\SharedContracts\\YourInterview.SharedContracts.csproj" />']
    if os.path.exists(os.path.join(SRC, 'Services.Analysis.Shared')) and d != 'Services.Analysis.Shared':
        r.append('    <ProjectReference Include="..\\Services.Analysis.Shared\\YourInterview.Services.Analysis.Shared.csproj" />')
    return '\n'.join(r)

for d in SERVICES:
    path = os.path.join(SRC, d)
    cs = glob.glob(os.path.join(path, '*.csproj'))
    if not cs:
        print('skip (no csproj):', d); continue
    name = os.path.basename(cs[0])[:-len('.csproj')]
    sdk = 'Microsoft.NET.Sdk.Web' if not d == 'BuildingBlocks' else 'Microsoft.NET.Sdk'
    extra = ''
    if d == 'Services.Identity':
        extra = """
    <PackageReference Include="Konscious.Security.Cryptography.Argon2" />
    <PackageReference Include="System.IdentityModel.Tokens.Jwt" />
    <PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" />"""
    content = f"""<Project Sdk="{sdk}">

  <ItemGroup>
{SERVICE_PKGS}{extra}
  </ItemGroup>

  <ItemGroup>
{refs(d)}
  </ItemGroup>

</Project>
"""
    with open(cs[0], 'w') as f:
        f.write(content)
    print('wrote', cs[0])
