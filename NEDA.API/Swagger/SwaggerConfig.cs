using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Swashbuckle.AspNetCore.SwaggerUI;
using NEDA.API.Common;
using NEDA.API.DTOs;
using NEDA.Core.Common.Constants;

namespace NEDA.API.Swagger;
{
    /// <summary>
    /// Swagger configuration for NEDA API documentation;
    /// Provides comprehensive API documentation with authentication, examples, and customization;
    /// </summary>
    public static class SwaggerConfig;
    {
        /// <summary>
        /// Add Swagger services to the DI container;
        /// </summary>
        public static IServiceCollection AddNEDASwagger(this IServiceCollection services)
        {
            services.AddSwaggerGen(options =>
            {
                // Configure API versioning;
                options.OperationFilter<SwaggerDefaultValues>();
                options.DocumentFilter<SwaggerDocumentFilter>();

                // Add XML comments;
                var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                if (File.Exists(xmlPath))
                {
                    options.IncludeXmlComments(xmlPath);
                }

                // Add security definitions;
                ConfigureSecurity(options);

                // Add custom filters;
                options.OperationFilter<AuthorizationHeaderFilter>();
                options.OperationFilter<CorrelationIdHeaderFilter>();
                options.OperationFilter<RequestIdHeaderFilter>();
                options.SchemaFilter<EnumSchemaFilter>();
                options.SchemaFilter<ExampleSchemaFilter>();

                // Configure Swagger options;
                ConfigureSwaggerOptions(options);

                // Add custom mappings;
                ConfigureCustomMappings(options);
            });

            services.AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerOptions>();
            services.AddApiVersioning(options =>
            {
                options.ReportApiVersions = true;
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.DefaultApiVersion = new Microsoft.AspNetCore.Mvc.ApiVersion(1, 0);
            });

            services.AddVersionedApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            });

            return services;
        }

        /// <summary>
        /// Configure Swagger in the request pipeline;
        /// </summary>
        public static IApplicationBuilder UseNEDASwagger(this IApplicationBuilder app, IApiVersionDescriptionProvider provider)
        {
            app.UseSwagger(options =>
            {
                options.RouteTemplate = "api-docs/{documentName}/swagger.json";
                options.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
                {
                    swaggerDoc.Servers = new List<OpenApiServer>
                    {
                        new OpenApiServer;
                        {
                            Url = $"{httpReq.Scheme}://{httpReq.Host.Value}",
                            Description = GetEnvironmentDescription()
                        }
                    };
                });
            });

            app.UseSwaggerUI(options =>
            {
                // Generate Swagger endpoints for all versions;
                foreach (var description in provider.ApiVersionDescriptions.OrderByDescending(v => v.ApiVersion))
                {
                    options.SwaggerEndpoint(
                        $"/api-docs/{description.GroupName}/swagger.json",
                        $"NEDA API {description.GroupName.ToUpperInvariant()}");

                    // Configure OAuth;
                    options.OAuthClientId("swagger-ui");
                    options.OAuthClientSecret("swagger-ui-secret");
                    options.OAuthRealm("NEDA-API");
                    options.OAuthAppName("NEDA API Swagger UI");
                    options.OAuthScopeSeparator(" ");
                    options.OAuthUsePkce();
                }

                // Configure UI options;
                ConfigureSwaggerUIOptions(options);

                // Custom index page;
                options.IndexStream = () =>
                    typeof(SwaggerConfig).Assembly.GetManifestResourceStream("NEDA.API.Swagger.CustomIndex.html");
            });

            return app;
        }

        #region Configuration Methods;
        private static void ConfigureSecurity(SwaggerGenOptions options)
        {
            // JWT Bearer Authentication;
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme;
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Enter your JWT token in the format: Bearer {token}"
            });

            // API Key Authentication;
            options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme;
            {
                Name = "X-API-Key",
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Description = "Enter your API key"
            });

            // OAuth2 Authentication;
            options.AddSecurityDefinition("OAuth2", new OpenApiSecurityScheme;
            {
                Type = SecuritySchemeType.OAuth2,
                Flows = new OpenApiOAuthFlows;
                {
                    AuthorizationCode = new OpenApiOAuthFlow;
                    {
                        AuthorizationUrl = new Uri("/api/auth/authorize", UriKind.Relative),
                        TokenUrl = new Uri("/api/auth/token", UriKind.Relative),
                        Scopes = new Dictionary<string, string>
                        {
                            { "api.read", "Read access to API" },
                            { "api.write", "Write access to API" },
                            { "api.admin", "Admin access to API" }
                        }
                    },
                    ClientCredentials = new OpenApiOAuthFlow;
                    {
                        TokenUrl = new Uri("/api/auth/token", UriKind.Relative),
                        Scopes = new Dictionary<string, string>
                        {
                            { "api.read", "Read access to API" },
                            { "api.write", "Write access to API" }
                        }
                    }
                }
            });

            // Apply security requirements globally;
            options.AddSecurityRequirement(new OpenApiSecurityRequirement;
            {
                {
                    new OpenApiSecurityScheme;
                    {
                        Reference = new OpenApiReference;
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    new[] { "api.read", "api.write" }
                }
            });
        }

        private static void ConfigureSwaggerOptions(SwaggerGenOptions options)
        {
            options.SwaggerDoc("v1", new OpenApiInfo;
            {
                Title = "NEDA API",
                Version = "v1.0.0",
                Description = @"# Neuro-Evolving Digital Assistant API;
                
## Overview;
The NEDA API provides comprehensive access to the Neuro-Evolving Digital Assistant platform, enabling AI-powered automation, project management, and system control.

## Key Features;
- **AI-Powered Command Execution**: Execute complex commands using natural language;
- **Project Management**: Create, manage, and collaborate on projects;
- **System Control**: Monitor and control system resources;
- **Real-time Analytics**: Get insights and performance metrics;
- **Security & Compliance**: Enterprise-grade security features;

## Authentication;
The API supports multiple authentication methods:
1. **JWT Bearer Tokens** - For user authentication;
2. **API Keys** - For service-to-service communication;
3. **OAuth2** - For third-party integrations;

## Rate Limiting;
- Default: 60 requests per minute;
- Burst: 100 requests;
- Contact support for higher limits;

## Support;
- Email: support@neda.ai;
- Documentation: https://docs.neda.ai;
- Status: https://status.neda.ai",
                TermsOfService = new Uri("https://neda.ai/terms"),
                Contact = new OpenApiContact;
                {
                    Name = "NEDA Support",
                    Email = "support@neda.ai",
                    Url = new Uri("https://support.neda.ai")
                },
                License = new OpenApiLicense;
                {
                    Name = "NEDA Enterprise License",
                    Url = new Uri("https://neda.ai/license")
                }
            });

            options.SwaggerDoc("v2", new OpenApiInfo;
            {
                Title = "NEDA API v2",
                Version = "v2.0.0-beta",
                Description = "Beta version with new features and improvements",
                TermsOfService = new Uri("https://neda.ai/terms"),
                Contact = new OpenApiContact;
                {
                    Name = "NEDA Development Team",
                    Email = "dev@neda.ai"
                }
            });

            // Customize operation IDs;
            options.CustomOperationIds(apiDesc =>
                apiDesc.TryGetMethodInfo(out MethodInfo methodInfo) ? methodInfo.Name : null);

            // Enable annotations;
            options.EnableAnnotations();

            // Add custom filters;
            options.SchemaFilter<ResponseSchemaFilter>();
            options.OperationFilter<ContentTypeOperationFilter>();

            // Map specific types;
            options.MapType<TimeSpan>(() => new OpenApiSchema;
            {
                Type = "string",
                Format = "duration",
                Example = new OpenApiString("00:00:30")
            });

            options.MapType<DateTime>(() => new OpenApiSchema;
            {
                Type = "string",
                Format = "date-time",
                Example = new OpenApiString("2024-01-15T12:00:00Z")
            });
        }

        private static void ConfigureSwaggerUIOptions(Swashbuckle.AspNetCore.SwaggerUI.SwaggerUIOptions options)
        {
            options.DocumentTitle = "NEDA API Documentation";
            options.DefaultModelExpandDepth = 2;
            options.DefaultModelRendering = ModelRendering.Model;
            options.DefaultModelsExpandDepth = 2;
            options.DisplayOperationId = true;
            options.DisplayRequestDuration = true;
            options.DocExpansion = DocExpansion.List;
            options.EnableDeepLinking = true;
            options.EnableFilter = true;
            options.MaxDisplayedTags = 20;
            options.ShowExtensions = true;
            options.ShowCommonExtensions = true;
            options.EnableValidator = true;
            options.SupportedSubmitMethods = SubmitMethod.Get | SubmitMethod.Post |
                                            SubmitMethod.Put | SubmitMethod.Delete |
                                            SubmitMethod.Patch;

            // Add custom styles and scripts;
            options.InjectStylesheet("/swagger/custom-styles.css");
            options.InjectJavascript("/swagger/custom-scripts.js");

            // Configure OAuth;
            options.OAuthConfigObject = new OAuthConfigObject;
            {
                AppName = "NEDA API Swagger UI",
                ClientId = "swagger-ui",
                ClientSecret = "swagger-ui-secret",
                Realm = "NEDA-API",
                ScopeSeparator = " ",
                UsePkceWithAuthorizationCodeGrant = true;
            };
        }

        private static void ConfigureCustomMappings(SwaggerGenOptions options)
        {
            // Add response examples;
            options.ExampleFilters();

            // Customize schema generation;
            options.UseAllOfToExtendReferenceSchemas();
            options.UseOneOfForPolymorphism();

            // Configure polymorphism;
            options.SelectSubTypesUsing(baseType =>
            {
                if (baseType == typeof(BaseResponse))
                {
                    return new[]
                    {
                        typeof(ErrorResponse),
                        typeof(OperationResponse),
                        typeof(ValidationResponse),
                        typeof(AuthResponse)
                    };
                }

                if (baseType == typeof(CommandResponse))
                {
                    return new[]
                    {
                        typeof(BatchCommandResponse),
                        typeof(CommandStatusResponse)
                    };
                }

                return Enumerable.Empty<Type>();
            });

            // Tag ordering;
            options.TagActionsBy(api =>
            {
                var controllerName = api.ActionDescriptor.RouteValues["controller"];
                return new[] { controllerName };
            });

            options.OrderActionsBy(apiDesc =>
                $"{apiDesc.ActionDescriptor.RouteValues["controller"]}_{apiDesc.HttpMethod}");
        }

        private static string GetEnvironmentDescription()
        {
            return Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") switch;
            {
                "Development" => "Development Environment",
                "Staging" => "Staging Environment",
                "Production" => "Production Environment",
                _ => "Local Environment"
            };
        }
        #endregion;
    }

    #region Swagger Filters and Configuration Classes;
    /// <summary>
    /// Configure Swagger options for each API version;
    /// </summary>
    public class ConfigureSwaggerOptions : IConfigureOptions<SwaggerGenOptions>
    {
        private readonly IApiVersionDescriptionProvider _provider;

        public ConfigureSwaggerOptions(IApiVersionDescriptionProvider provider)
        {
            _provider = provider;
        }

        public void Configure(SwaggerGenOptions options)
        {
            foreach (var description in _provider.ApiVersionDescriptions)
            {
                options.SwaggerDoc(description.GroupName, CreateInfoForApiVersion(description));
            }
        }

        private OpenApiInfo CreateInfoForApiVersion(ApiVersionDescription description)
        {
            var info = new OpenApiInfo;
            {
                Title = $"NEDA API {description.ApiVersion}",
                Version = description.ApiVersion.ToString(),
                Description = GetVersionDescription(description),
                Contact = new OpenApiContact;
                {
                    Name = "NEDA Development Team",
                    Email = "dev@neda.ai",
                    Url = new Uri("https://neda.ai")
                }
            };

            if (description.IsDeprecated)
            {
                info.Description += " **This API version has been deprecated.**";
            }

            return info;
        }

        private string GetVersionDescription(ApiVersionDescription description)
        {
            return description.ApiVersion.MajorVersion switch;
            {
                1 => "Initial stable release with core NEDA functionality.",
                2 => "Beta release with new AI features and enhanced performance.",
                _ => "Latest API version with all features."
            };
        }
    }

    /// <summary>
    /// Apply default values to operations;
    /// </summary>
    public class SwaggerDefaultValues : IOperationFilter;
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var apiDescription = context.ApiDescription;

            operation.Deprecated |= apiDescription.IsDeprecated();

            if (operation.Parameters == null)
                return;

            foreach (var parameter in operation.Parameters)
            {
                var description = apiDescription.ParameterDescriptions;
                    .First(p => p.Name == parameter.Name);

                parameter.Description ??= description.ModelMetadata?.Description;

                if (parameter.Schema.Default == null && description.DefaultValue != null)
                {
                    parameter.Schema.Default = new OpenApiString(description.DefaultValue.ToString());
                }

                parameter.Required |= description.IsRequired;
            }
        }
    }

    /// <summary>
    /// Add authorization header parameter to operations;
    /// </summary>
    public class AuthorizationHeaderFilter : IOperationFilter;
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (operation.Parameters == null)
                operation.Parameters = new List<OpenApiParameter>();

            // Skip for auth endpoints;
            if (context.ApiDescription.RelativePath?.Contains("auth") == true)
                return;

            operation.Parameters.Add(new OpenApiParameter;
            {
                Name = "Authorization",
                In = ParameterLocation.Header,
                Description = "JWT Bearer token",
                Required = false,
                Schema = new OpenApiSchema;
                {
                    Type = "string",
                    Default = new OpenApiString("Bearer ")
                }
            });
        }
    }

    /// <summary>
    /// Add correlation ID header parameter;
    /// </summary>
    public class CorrelationIdHeaderFilter : IOperationFilter;
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (operation.Parameters == null)
                operation.Parameters = new List<OpenApiParameter>();

            operation.Parameters.Add(new OpenApiParameter;
            {
                Name = "X-Correlation-ID",
                In = ParameterLocation.Header,
                Description = "Correlation ID for request tracking",
                Required = false,
                Schema = new OpenApiSchema;
                {
                    Type = "string",
                    Format = "uuid",
                    Example = new OpenApiString(Guid.NewGuid().ToString())
                }
            });
        }
    }

    /// <summary>
    /// Add request ID header parameter;
    /// </summary>
    public class RequestIdHeaderFilter : IOperationFilter;
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (operation.Parameters == null)
                operation.Parameters = new List<OpenApiParameter>();

            operation.Parameters.Add(new OpenApiParameter;
            {
                Name = "X-Request-ID",
                In = ParameterLocation.Header,
                Description = "Unique request identifier",
                Required = false,
                Schema = new OpenApiSchema;
                {
                    Type = "string",
                    Format = "uuid",
                    Example = new OpenApiString(Guid.NewGuid().ToString())
                }
            });
        }
    }

    /// <summary>
    /// Customize enum display in Swagger;
    /// </summary>
    public class EnumSchemaFilter : ISchemaFilter;
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (context.Type.IsEnum)
            {
                schema.Enum.Clear();

                foreach (var enumValue in Enum.GetValues(context.Type))
                {
                    var name = enumValue.ToString();
                    var memberInfo = context.Type.GetMember(name).First();
                    var description = memberInfo.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;

                    schema.Enum.Add(new OpenApiString($"{enumValue} - {description ?? name}"));
                }
            }
        }
    }

    /// <summary>
    /// Add examples to schemas;
    /// </summary>
    public class ExampleSchemaFilter : ISchemaFilter;
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (context.Type == typeof(CommandRequest))
            {
                schema.Example = new OpenApiObject;
                {
                    ["commandName"] = new OpenApiString("process_image"),
                    ["parameters"] = new OpenApiObject;
                    {
                        ["imageUrl"] = new OpenApiString("https://example.com/image.jpg"),
                        ["operation"] = new OpenApiString("resize"),
                        ["width"] = new OpenApiInteger(800),
                        ["height"] = new OpenApiInteger(600)
                    },
                    ["priority"] = new OpenApiString("Normal"),
                    ["clientId"] = new OpenApiString("client-12345")
                };
            }
            else if (context.Type == typeof(Project))
            {
                schema.Example = new OpenApiObject;
                {
                    ["id"] = new OpenApiString("proj-12345"),
                    ["name"] = new OpenApiString("AI Animation Project"),
                    ["description"] = new OpenApiString("Creating animated sequences using AI"),
                    ["type"] = new OpenApiString("Animation"),
                    ["status"] = new OpenApiString("Active"),
                    ["createdBy"] = new OpenApiString("user-12345"),
                    ["createdAt"] = new OpenApiString("2024-01-15T10:30:00Z"),
                    ["version"] = new OpenApiInteger(1)
                };
            }
            else if (context.Type == typeof(ErrorResponse))
            {
                schema.Example = new OpenApiObject;
                {
                    ["success"] = new OpenApiBoolean(false),
                    ["errorCode"] = new OpenApiString("VALIDATION_ERROR"),
                    ["message"] = new OpenApiString("Invalid input parameters"),
                    ["errorType"] = new OpenApiString("Validation"),
                    ["timestamp"] = new OpenApiString("2024-01-15T12:00:00Z"),
                    ["correlationId"] = new OpenApiString(Guid.NewGuid().ToString()),
                    ["validationErrors"] = new OpenApiArray;
                    {
                        new OpenApiObject;
                        {
                            ["field"] = new OpenApiString("email"),
                            ["message"] = new OpenApiString("Email address is required"),
                            ["severity"] = new OpenApiString("Error")
                        }
                    }
                };
            }
        }
    }

    /// <summary>
    /// Customize response schemas;
    /// </summary>
    public class ResponseSchemaFilter : ISchemaFilter;
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (context.Type == typeof(Response<>) ||
                context.Type == typeof(PaginatedResponse<>) ||
                context.Type == typeof(SearchResponse<>))
            {
                // Ensure response schemas include metadata;
                if (!schema.Properties.ContainsKey("metadata"))
                {
                    schema.Properties.Add("metadata", new OpenApiSchema;
                    {
                        Type = "object",
                        Description = "Response metadata including timing and pagination info"
                    });
                }
            }
        }
    }

    /// <summary>
    /// Add content type to operations;
    /// </summary>
    public class ContentTypeOperationFilter : IOperationFilter;
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (operation.RequestBody == null)
                return;

            foreach (var content in operation.RequestBody.Content)
            {
                if (content.Key == "application/json")
                {
                    content.Value.Schema.Example = GetExampleForOperation(context);
                }
            }
        }

        private OpenApiObject GetExampleForOperation(OperationFilterContext context)
        {
            var operationId = context.ApiDescription.ActionDescriptor.RouteValues["action"];

            return operationId switch;
            {
                "ExecuteCommand" => new OpenApiObject;
                {
                    ["commandName"] = new OpenApiString("analyze_data"),
                    ["parameters"] = new OpenApiObject;
                    {
                        ["datasetId"] = new OpenApiString("data-12345"),
                        ["analysisType"] = new OpenApiString("statistical"),
                        ["outputFormat"] = new OpenApiString("json")
                    }
                },
                "CreateProject" => new OpenApiObject;
                {
                    ["name"] = new OpenApiString("New Project"),
                    ["description"] = new OpenApiString("Project description"),
                    ["type"] = new OpenApiString("General"),
                    ["settings"] = new OpenApiObject()
                },
                _ => new OpenApiObject()
            };
        }
    }

    /// <summary>
    /// Custom document filter for global modifications;
    /// </summary>
    public class SwaggerDocumentFilter : IDocumentFilter;
    {
        public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        {
            // Add global tags;
            swaggerDoc.Tags = new List<OpenApiTag>
            {
                new OpenApiTag { Name = "Authentication", Description = "User authentication and authorization" },
                new OpenApiTag { Name = "Projects", Description = "Project management operations" },
                new OpenApiTag { Name = "Commands", Description = "AI command execution" },
                new OpenApiTag { Name = "System", Description = "System monitoring and administration" },
                new OpenApiTag { Name = "Files", Description = "File upload and management" },
                new OpenApiTag { Name = "Users", Description = "User management" }
            };

            // Add global parameters;
            swaggerDoc.Components ??= new OpenApiComponents();
            swaggerDoc.Components.Parameters ??= new Dictionary<string, OpenApiParameter>();

            // Add server variables;
            if (swaggerDoc.Servers == null)
            {
                swaggerDoc.Servers = new List<OpenApiServer>();
            }

            // Sort paths alphabetically;
            var paths = swaggerDoc.Paths.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value);
            swaggerDoc.Paths = new OpenApiPaths();
            foreach (var path in paths)
            {
                swaggerDoc.Paths.Add(path.Key, path.Value);
            }
        }
    }
    #endregion;
}
