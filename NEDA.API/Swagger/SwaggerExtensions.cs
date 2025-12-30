using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using NEDA.API.Common;
using NEDA.API.DTOs;
using NEDA.Core.Common.Constants;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;
using Swashbuckle.AspNetCore.SwaggerUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters;

namespace NEDA.API.Swagger;
{
    /// <summary>
    /// Swagger extension methods for enhanced API documentation;
    /// Provides additional functionality, customization, and utilities for Swagger;
    /// </summary>
    public static class SwaggerExtensions;
    {
        #region Service Collection Extensions;
        /// <summary>
        /// Adds enhanced Swagger services with NEDA-specific configurations;
        /// </summary>
        public static IServiceCollection AddEnhancedSwagger(this IServiceCollection services,
            Action<SwaggerGenOptions> configure = null)
        {
            services.AddSwaggerGen(options =>
            {
                // Apply NEDA-specific configurations;
                ConfigureNEDASwagger(options);

                // Apply custom configuration if provided;
                configure?.Invoke(options);

                // Add additional NEDA-specific filters;
                AddNEDAFilters(options);
            });

            // Register Swagger services;
            services.AddSwaggerGenNewtonsoftSupport();

            return services;
        }

        /// <summary>
        /// Adds Swagger with custom document generation;
        /// </summary>
        public static IServiceCollection AddSwaggerWithCustomDocs(this IServiceCollection services,
            params OpenApiInfo[] apiInfos)
        {
            services.AddSwaggerGen(options =>
            {
                foreach (var apiInfo in apiInfos)
                {
                    options.SwaggerDoc(apiInfo.Version, apiInfo);
                }

                // Apply NEDA configurations;
                ConfigureNEDASwagger(options);
            });

            return services;
        }

        /// <summary>
        /// Adds Swagger with embedded resources support;
        /// </summary>
        public static IServiceCollection AddSwaggerWithEmbeddedResources(this IServiceCollection services)
        {
            services.AddSwaggerGen(options =>
            {
                // Include embedded XML documentation;
                var assembly = Assembly.GetExecutingAssembly();
                var xmlFiles = assembly.GetManifestResourceNames()
                    .Where(name => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

                foreach (var xmlFile in xmlFiles)
                {
                    using var stream = assembly.GetManifestResourceStream(xmlFile);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        options.IncludeXmlComments(() => new System.Xml.XPath.XPathDocument(reader), includeControllerXmlComments: true);
                    }
                }

                ConfigureNEDASwagger(options);
            });

            return services;
        }
        #endregion;

        #region Application Builder Extensions;
        /// <summary>
        /// Uses enhanced Swagger UI with NEDA customizations;
        /// </summary>
        public static IApplicationBuilder UseEnhancedSwaggerUI(this IApplicationBuilder app,
            Action<SwaggerUIOptions> configure = null)
        {
            app.UseSwagger(options =>
            {
                options.RouteTemplate = "api-docs/{documentName}/swagger.json";
                options.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
                {
                    // Add server information;
                    swaggerDoc.Servers = new List<OpenApiServer>
                    {
                        new OpenApiServer;
                        {
                            Url = $"{httpReq.Scheme}://{httpReq.Host.Value}",
                            Description = GetEnvironmentServerDescription()
                        }
                    };

                    // Add custom extensions;
                    AddCustomExtensions(swaggerDoc);
                });
            });

            app.UseSwaggerUI(options =>
            {
                // Apply NEDA UI configurations;
                ConfigureNEDASwaggerUI(options);

                // Apply custom configuration;
                configure?.Invoke(options);

                // Serve custom assets;
                ServeCustomAssets(app, options);
            });

            return app;
        }

        /// <summary>
        /// Uses Swagger with versioned API explorer;
        /// </summary>
        public static IApplicationBuilder UseVersionedSwaggerUI(this IApplicationBuilder app,
            string[] versions = null)
        {
            var apiVersionDescriptions = app.ApplicationServices;
                .GetService<Microsoft.AspNetCore.Mvc.ApiExplorer.IApiVersionDescriptionProvider>();

            app.UseSwaggerUI(options =>
            {
                versions ??= new[] { "v1", "v2" };

                foreach (var version in versions)
                {
                    options.SwaggerEndpoint(
                        $"/api-docs/{version}/swagger.json",
                        $"NEDA API {version.ToUpperInvariant()}");
                }

                ConfigureNEDASwaggerUI(options);
            });

            return app;
        }

        /// <summary>
        /// Uses Swagger with custom middleware pipeline;
        /// </summary>
        public static IApplicationBuilder UseSwaggerWithCustomPipeline(this IApplicationBuilder app)
        {
            // Add custom Swagger endpoint;
            app.Map("/swagger-custom", builder =>
            {
                builder.Use(async (context, next) =>
                {
                    if (context.Request.Path == "/")
                    {
                        context.Response.Redirect("/swagger/index.html");
                        return;
                    }
                    await next();
                });

                builder.UseSwagger();
                builder.UseSwaggerUI(options =>
                {
                    ConfigureNEDASwaggerUI(options);
                    options.ConfigObject.AdditionalItems["customPipeline"] = true;
                });
            });

            return app;
        }

        /// <summary>
        /// Serves Swagger custom assets from embedded resources;
        /// </summary>
        public static IApplicationBuilder UseSwaggerCustomAssets(this IApplicationBuilder app)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.Contains("Swagger.Assets", StringComparison.OrdinalIgnoreCase));

            foreach (var resourceName in resourceNames)
            {
                var relativePath = resourceName;
                    .Replace("NEDA.API.Swagger.Assets.", "")
                    .Replace('.', '/');

                var directory = Path.GetDirectoryName(relativePath);
                var fileName = Path.GetFileName(relativePath);

                if (!string.IsNullOrEmpty(directory))
                {
                    app.UseStaticFiles(new StaticFileOptions;
                    {
                        FileProvider = new EmbeddedFileProvider(assembly, $"NEDA.API.Swagger.Assets.{directory.Replace('/', '.')}"),
                        RequestPath = $"/swagger/assets/{directory}"
                    });
                }
            }

            return app;
        }
        #endregion;

        #region Swagger Gen Extensions;
        /// <summary>
        /// Adds NEDA-specific examples to Swagger;
        /// </summary>
        public static SwaggerGenOptions AddNEDAExamples(this SwaggerGenOptions options)
        {
            options.ExampleFilters();

            // Add operation examples;
            options.OperationFilter<NEDAOperationExamplesFilter>();

            // Add schema examples;
            options.SchemaFilter<NEDASchemaExamplesFilter>();

            return options;
        }

        /// <summary>
        /// Adds NEDA security schemes to Swagger;
        /// </summary>
        public static SwaggerGenOptions AddNEDASecuritySchemes(this SwaggerGenOptions options)
        {
            // NEDA JWT Scheme;
            options.AddSecurityDefinition("NEDA-JWT", new OpenApiSecurityScheme;
            {
                Name = "NEDA-Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "NEDA JWT Token Authentication"
            });

            // NEDA API Key Scheme;
            options.AddSecurityDefinition("NEDA-API-Key", new OpenApiSecurityScheme;
            {
                Name = "X-NEDA-API-Key",
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Description = "NEDA API Key Authentication"
            });

            // NEDA OAuth2 Scheme;
            options.AddSecurityDefinition("NEDA-OAuth2", new OpenApiSecurityScheme;
            {
                Type = SecuritySchemeType.OAuth2,
                Flows = new OpenApiOAuthFlows;
                {
                    Implicit = new OpenApiOAuthFlow;
                    {
                        AuthorizationUrl = new Uri("/api/auth/oauth/authorize", UriKind.Relative),
                        Scopes = new Dictionary<string, string>
                        {
                            ["api:read"] = "Read access",
                            ["api:write"] = "Write access",
                            ["api:admin"] = "Admin access"
                        }
                    }
                }
            });

            // Apply security requirements;
            options.AddSecurityRequirement(new OpenApiSecurityRequirement;
            {
                {
                    new OpenApiSecurityScheme;
                    {
                        Reference = new OpenApiReference;
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "NEDA-JWT"
                        }
                    },
                    new[] { "api:read", "api:write" }
                }
            });

            return options;
        }

        /// <summary>
        /// Adds NEDA response conventions to Swagger;
        /// </summary>
        public static SwaggerGenOptions AddNEDAResponseConventions(this SwaggerGenOptions options)
        {
            options.OperationFilter<NEDAResponseConventionsFilter>();

            // Map NEDA-specific response types;
            options.MapType<NEDAErrorResponse>(() => new OpenApiSchema;
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["errorCode"] = new OpenApiSchema { Type = "string" },
                    ["errorMessage"] = new OpenApiSchema { Type = "string" },
                    ["correlationId"] = new OpenApiSchema { Type = "string", Format = "uuid" },
                    ["timestamp"] = new OpenApiSchema { Type = "string", Format = "date-time" }
                }
            });

            return options;
        }

        /// <summary>
        /// Adds NEDA-specific tags to Swagger;
        /// </summary>
        public static SwaggerGenOptions AddNEDATags(this SwaggerGenOptions options)
        {
            options.TagActionsBy(api =>
            {
                var controllerName = api.ActionDescriptor.RouteValues["controller"];
                var tags = new List<string> { controllerName };

                // Add NEDA-specific tags based on controller;
                if (controllerName.Contains("Command", StringComparison.OrdinalIgnoreCase))
                {
                    tags.Add("AI-Commands");
                }
                else if (controllerName.Contains("Project", StringComparison.OrdinalIgnoreCase))
                {
                    tags.Add("Project-Management");
                }
                else if (controllerName.Contains("System", StringComparison.OrdinalIgnoreCase))
                {
                    tags.Add("System-Admin");
                }

                return tags;
            });

            return options;
        }

        /// <summary>
        /// Configures Swagger to use NEDA naming conventions;
        /// </summary>
        public static SwaggerGenOptions UseNEDANamingConventions(this SwaggerGenOptions options)
        {
            options.CustomSchemaIds(type => type.Name.Replace("DTO", "").Replace("Response", "").Replace("Request", ""));
            options.CustomOperationIds(apiDesc =>
            {
                var controllerName = apiDesc.ActionDescriptor.RouteValues["controller"];
                var actionName = apiDesc.ActionDescriptor.RouteValues["action"];
                return $"{controllerName}_{actionName}";
            });

            return options;
        }
        #endregion;

        #region Swagger UI Extensions;
        /// <summary>
        /// Configures Swagger UI with NEDA theme;
        /// </summary>
        public static SwaggerUIOptions UseNEDATheme(this SwaggerUIOptions options)
        {
            options.InjectStylesheet("/swagger/neda-theme.css");
            options.InjectJavascript("/swagger/neda-extensions.js");

            options.ConfigObject.AdditionalItems["theme"] = "neda";
            options.ConfigObject.AdditionalItems["branding"] = new;
            {
                logoUrl = "/swagger/neda-logo.png",
                title = "NEDA API Documentation"
            };

            return options;
        }

        /// <summary>
        /// Adds NEDA-specific presets to Swagger UI;
        /// </summary>
        public static SwaggerUIOptions AddNEDAPresets(this SwaggerUIOptions options)
        {
            options.ConfigObject.AdditionalItems["presets"] = new List<object>
            {
                new;
                {
                    name = "NEDA-Default",
                    value = new;
                    {
                        headers = new Dictionary<string, string>
                        {
                            ["X-NEDA-Client"] = "Swagger-UI",
                            ["Accept"] = "application/json"
                        }
                    }
                },
                new;
                {
                    name = "NEDA-Admin",
                    value = new;
                    {
                        headers = new Dictionary<string, string>
                        {
                            ["X-NEDA-Client"] = "Admin-Swagger-UI",
                            ["Authorization"] = "Bearer admin-token-placeholder"
                        }
                    }
                }
            };

            return options;
        }

        /// <summary>
        /// Enables NEDA-specific Swagger UI features;
        /// </summary>
        public static SwaggerUIOptions EnableNEDAFeatures(this SwaggerUIOptions options)
        {
            options.EnablePersistAuthorization();
            options.EnableValidator();
            options.EnableDeepLinking();
            options.DisplayRequestDuration();
            options.ShowExtensions();
            options.ShowCommonExtensions();

            options.ConfigObject.AdditionalItems["features"] = new;
            {
                requestSnippets = true,
                syntaxHighlighting = true,
                autoComplete = true,
                modelRendering = "example"
            };

            return options;
        }

        /// <summary>
        /// Configures OAuth for NEDA API;
        /// </summary>
        public static SwaggerUIOptions ConfigureNEDAOAuth(this SwaggerUIOptions options)
        {
            options.OAuthConfigObject = new OAuthConfigObject;
            {
                AppName = "NEDA API Explorer",
                ClientId = "swagger-ui",
                ClientSecret = "swagger-ui-secret",
                Realm = "NEDA",
                ScopeSeparator = " ",
                AdditionalQueryStringParams = new Dictionary<string, string>
                {
                    ["audience"] = "neda-api"
                },
                UseBasicAuthenticationWithAccessCodeGrant = false,
                UsePkceWithAuthorizationCodeGrant = true;
            };

            return options;
        }
        #endregion;

        #region Private Configuration Methods;
        private static void ConfigureNEDASwagger(SwaggerGenOptions options)
        {
            // Add NEDA-specific document info;
            options.SwaggerDoc("v1", new OpenApiInfo;
            {
                Title = "NEDA API",
                Version = "v1",
                Description = CreateNEDADescription(),
                Contact = new OpenApiContact;
                {
                    Name = "NEDA Support",
                    Email = "api-support@neda.ai",
                    Url = new Uri("https://support.neda.ai")
                },
                License = new OpenApiLicense;
                {
                    Name = "NEDA Enterprise License",
                    Url = new Uri("https://neda.ai/license")
                },
                Extensions = new Dictionary<string, IOpenApiExtension>
                {
                    ["x-neda-api-version"] = new OpenApiString("1.0.0"),
                    ["x-neda-api-status"] = new OpenApiString("stable")
                }
            });

            // Add NEDA-specific security;
            options.AddNEDASecuritySchemes();

            // Add NEDA examples;
            options.AddNEDAExamples();

            // Add NEDA response conventions;
            options.AddNEDAResponseConventions();

            // Add NEDA tags;
            options.AddNEDATags();

            // Use NEDA naming conventions;
            options.UseNEDANamingConventions();

            // Include XML comments;
            IncludeNEDAXmlComments(options);
        }

        private static void AddNEDAFilters(SwaggerGenOptions options)
        {
            options.OperationFilter<NEDAAuthorizationFilter>();
            options.OperationFilter<NEDACorrelationFilter>();
            options.OperationFilter<NEDARequestIdFilter>();
            options.DocumentFilter<NEDADocumentFilter>();
            options.SchemaFilter<NEDASchemaFilter>();
        }

        private static void ConfigureNEDASwaggerUI(Swashbuckle.AspNetCore.SwaggerUI.SwaggerUIOptions options)
        {
            options.DocumentTitle = "NEDA API Documentation";
            options.DefaultModelExpandDepth = 3;
            options.DefaultModelsExpandDepth = 3;
            options.DefaultModelRendering = ModelRendering.Example;
            options.DisplayOperationId = true;
            options.DisplayRequestDuration = true;
            options.DocExpansion = DocExpansion.List;
            options.EnableDeepLinking = true;
            options.EnableFilter = true;
            options.MaxDisplayedTags = 25;
            options.ShowExtensions = true;
            options.ShowCommonExtensions = true;
            options.SupportedSubmitMethods = SubmitMethod.Get | SubmitMethod.Post |
                                           SubmitMethod.Put | SubmitMethod.Delete |
                                           SubmitMethod.Patch | SubmitMethod.Head;

            // Apply NEDA theme;
            options.UseNEDATheme();

            // Enable NEDA features;
            options.EnableNEDAFeatures();

            // Add NEDA presets;
            options.AddNEDAPresets();

            // Configure NEDA OAuth;
            options.ConfigureNEDAOAuth();
        }

        private static string CreateNEDADescription()
        {
            return @"
# Neuro-Evolving Digital Assistant API;

## Overview;
Welcome to the NEDA API documentation. This API provides access to the Neuro-Evolving Digital Assistant platform, enabling AI-powered automation, intelligent workflows, and system integration.

## Key Capabilities;
- **AI Command Execution**: Execute natural language commands with AI understanding;
- **Project Management**: Full lifecycle project management with collaboration features;
- **System Integration**: Integrate with external systems and services;
- **Real-time Analytics**: Monitor performance and get insights;
- **Security & Compliance**: Enterprise-grade security with audit trails;

## Getting Started;
1. **Obtain API Credentials**: Contact support@neda.ai for API keys;
2. **Authenticate**: Use JWT tokens or API keys for authentication;
3. **Explore Endpoints**: Start with the /api/system/health endpoint;
4. **Review Examples**: Check the example requests for each endpoint;

## Rate Limits;
- Standard: 60 requests/minute;
- Premium: 200 requests/minute;
- Enterprise: Custom limits available;

## Support;
- Email: api-support@neda.ai;
- Documentation: https://docs.neda.ai/api;
- Status: https://status.neda.ai;
- Community: https://community.neda.ai;
";
        }

        private static void IncludeNEDAXmlComments(SwaggerGenOptions options)
        {
            try
            {
                var basePath = AppContext.BaseDirectory;
                var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
                var xmlFile = $"{assemblyName}.xml";
                var xmlPath = Path.Combine(basePath, xmlFile);

                if (File.Exists(xmlPath))
                {
                    options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
                }

                // Include NEDA.Core XML comments if available;
                var coreXmlPath = Path.Combine(basePath, "NEDA.Core.xml");
                if (File.Exists(coreXmlPath))
                {
                    options.IncludeXmlComments(coreXmlPath);
                }
            }
            catch
            {
                // Silently fail if XML comments are not available;
            }
        }

        private static string GetEnvironmentServerDescription()
        {
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
            var description = environment switch;
            {
                "Production" => "Production Environment - Live API",
                "Staging" => "Staging Environment - Pre-production",
                "Development" => "Development Environment - Testing",
                _ => "Local Development Environment"
            };

            return $"{description} | NEDA API";
        }

        private static void AddCustomExtensions(OpenApiDocument swaggerDoc)
        {
            swaggerDoc.Extensions["x-neda-api-version"] = new OpenApiString("1.0.0");
            swaggerDoc.Extensions["x-neda-sdk-version"] = new OpenApiString("1.0.0");
            swaggerDoc.Extensions["x-neda-supported-languages"] = new OpenApiArray;
            {
                new OpenApiString("en-US"),
                new OpenApiString("tr-TR"),
                new OpenApiString("de-DE")
            };
            swaggerDoc.Extensions["x-neda-api-status"] = new OpenApiString("operational");
        }

        private static void ServeCustomAssets(IApplicationBuilder app, SwaggerUIOptions options)
        {
            // Serve custom CSS;
            app.UseStaticFiles(new StaticFileOptions;
            {
                FileProvider = new EmbeddedFileProvider(
                    Assembly.GetExecutingAssembly(),
                    "NEDA.API.Swagger.Assets"),
                RequestPath = "/swagger"
            });

            // Add custom JavaScript;
            options.InjectJavascript("/swagger/neda-custom.js");

            // Add custom CSS;
            options.InjectStylesheet("/swagger/neda-styles.css");
        }
        #endregion;

        #region Supporting Filter Classes;
        /// <summary>
        /// NEDA-specific operation examples filter;
        /// </summary>
        public class NEDAOperationExamplesFilter : IOperationFilter;
        {
            public void Apply(OpenApiOperation operation, OperationFilterContext context)
            {
                var controllerName = context.ApiDescription.ActionDescriptor.RouteValues["controller"];
                var actionName = context.ApiDescription.ActionDescriptor.RouteValues["action"];

                // Add NEDA-specific examples based on controller and action;
                if (controllerName == "CommandController" && actionName == "ExecuteCommand")
                {
                    operation.RequestBody.Content["application/json"].Example = new OpenApiObject;
                    {
                        ["commandName"] = new OpenApiString("generate_report"),
                        ["parameters"] = new OpenApiObject;
                        {
                            ["reportType"] = new OpenApiString("weekly"),
                            ["format"] = new OpenApiString("pdf"),
                            ["includeCharts"] = new OpenApiBoolean(true)
                        },
                        ["clientId"] = new OpenApiString("neda-web-client"),
                        ["priority"] = new OpenApiString("High")
                    };
                }
            }
        }

        /// <summary>
        /// NEDA-specific schema examples filter;
        /// </summary>
        public class NEDASchemaExamplesFilter : ISchemaFilter;
        {
            public void Apply(OpenApiSchema schema, SchemaFilterContext context)
            {
                if (context.Type == typeof(CommandResponse))
                {
                    schema.Example = new OpenApiObject;
                    {
                        ["success"] = new OpenApiBoolean(true),
                        ["commandId"] = new OpenApiString("cmd_123456789"),
                        ["commandName"] = new OpenApiString("process_data"),
                        ["result"] = new OpenApiObject;
                        {
                            ["processedItems"] = new OpenApiInteger(150),
                            ["successRate"] = new OpenApiDouble(98.5)
                        },
                        ["executionTime"] = new OpenApiString("00:00:05.123"),
                        ["timestamp"] = new OpenApiString("2024-01-15T14:30:00Z")
                    };
                }
            }
        }

        /// <summary>
        /// NEDA authorization filter;
        /// </summary>
        public class NEDAAuthorizationFilter : IOperationFilter;
        {
            public void Apply(OpenApiOperation operation, OperationFilterContext context)
            {
                // Add NEDA-specific authorization header;
                operation.Parameters.Add(new OpenApiParameter;
                {
                    Name = "X-NEDA-Authorization",
                    In = ParameterLocation.Header,
                    Description = "NEDA-specific authorization header",
                    Required = false,
                    Schema = new OpenApiSchema { Type = "string" }
                });
            }
        }

        /// <summary>
        /// NEDA correlation filter;
        /// </summary>
        public class NEDACorrelationFilter : IOperationFilter;
        {
            public void Apply(OpenApiOperation operation, OperationFilterContext context)
            {
                operation.Parameters.Add(new OpenApiParameter;
                {
                    Name = "X-NEDA-Correlation-ID",
                    In = ParameterLocation.Header,
                    Description = "NEDA correlation ID for distributed tracing",
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
        /// NEDA request ID filter;
        /// </summary>
        public class NEDARequestIdFilter : IOperationFilter;
        {
            public void Apply(OpenApiOperation operation, OperationFilterContext context)
            {
                operation.Parameters.Add(new OpenApiParameter;
                {
                    Name = "X-NEDA-Request-ID",
                    In = ParameterLocation.Header,
                    Description = "Unique request identifier for NEDA API",
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
        /// NEDA response conventions filter;
        /// </summary>
        public class NEDAResponseConventionsFilter : IOperationFilter;
        {
            public void Apply(OpenApiOperation operation, OperationFilterContext context)
            {
                // Ensure all operations have standard NEDA responses;
                if (!operation.Responses.ContainsKey("400"))
                {
                    operation.Responses.Add("400", new OpenApiResponse;
                    {
                        Description = "Bad Request - NEDA validation error",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType;
                            {
                                Schema = new OpenApiSchema;
                                {
                                    Reference = new OpenApiReference;
                                    {
                                        Type = ReferenceType.Schema,
                                        Id = "ErrorResponse"
                                    }
                                }
                            }
                        }
                    });
                }

                if (!operation.Responses.ContainsKey("500"))
                {
                    operation.Responses.Add("500", new OpenApiResponse;
                    {
                        Description = "Internal Server Error - NEDA system error",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType;
                            {
                                Schema = new OpenApiSchema;
                                {
                                    Reference = new OpenApiReference;
                                    {
                                        Type = ReferenceType.Schema,
                                        Id = "ErrorResponse"
                                    }
                                }
                            }
                        }
                    });
                }
            }
        }

        /// <summary>
        /// NEDA document filter;
        /// </summary>
        public class NEDADocumentFilter : IDocumentFilter;
        {
            public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
            {
                // Add NEDA-specific tags;
                swaggerDoc.Tags.Add(new OpenApiTag;
                {
                    Name = "NEDA-AI",
                    Description = "AI-powered command execution and processing"
                });

                swaggerDoc.Tags.Add(new OpenApiTag;
                {
                    Name = "NEDA-System",
                    Description = "System monitoring and administration"
                });

                // Add NEDA extensions;
                swaggerDoc.Extensions["x-neda-generated"] = new OpenApiString(DateTime.UtcNow.ToString("o"));
                swaggerDoc.Extensions["x-neda-api-family"] = new OpenApiString("Neuro-Evolving Digital Assistant");
            }
        }

        /// <summary>
        /// NEDA schema filter;
        /// </summary>
        public class NEDASchemaFilter : ISchemaFilter;
        {
            public void Apply(OpenApiSchema schema, SchemaFilterContext context)
            {
                // Add NEDA-specific schema properties;
                if (schema.Properties != null)
                {
                    foreach (var property in schema.Properties)
                    {
                        // Add NEDA-specific descriptions;
                        if (property.Key.Contains("Id", StringComparison.OrdinalIgnoreCase))
                        {
                            property.Value.Description += " (NEDA format: prefix_random)";
                        }

                        if (property.Key.Contains("Timestamp", StringComparison.OrdinalIgnoreCase))
                        {
                            property.Value.Description += " (ISO 8601 format - NEDA standard)";
                        }
                    }
                }
            }
        }
        #endregion;

        #region Supporting Classes;
        /// <summary>
        /// NEDA-specific error response;
        /// </summary>
        public class NEDAErrorResponse;
        {
            public string ErrorCode { get; set; }
            public string ErrorMessage { get; set; }
            public string CorrelationId { get; set; }
            public DateTime Timestamp { get; set; }
            public Dictionary<string, object> AdditionalInfo { get; set; }
        }

        /// <summary>
        /// Embedded file provider for Swagger assets;
        /// </summary>
        public class EmbeddedFileProvider : IFileProvider;
        {
            private readonly Assembly _assembly;
            private readonly string _baseNamespace;

            public EmbeddedFileProvider(Assembly assembly, string baseNamespace)
            {
                _assembly = assembly;
                _baseNamespace = baseNamespace;
            }

            public IFileInfo GetFileInfo(string subpath)
            {
                var resourcePath = $"{_baseNamespace}.{subpath.Replace('/', '.')}";
                var stream = _assembly.GetManifestResourceStream(resourcePath);

                if (stream == null)
                    return new NotFoundFileInfo(subpath);

                return new EmbeddedFileInfo(stream, subpath);
            }

            public IDirectoryContents GetDirectoryContents(string subpath) => null;
            public IChangeToken Watch(string filter) => null;
        }

        /// <summary>
        /// Embedded file info implementation;
        /// </summary>
        public class EmbeddedFileInfo : IFileInfo;
        {
            private readonly Stream _stream;
            private readonly string _name;

            public EmbeddedFileInfo(Stream stream, string name)
            {
                _stream = stream;
                _name = name;
                Length = stream.Length;
                LastModified = DateTimeOffset.UtcNow;
            }

            public bool Exists => true;
            public long Length { get; }
            public string PhysicalPath => null;
            public string Name => _name;
            public DateTimeOffset LastModified { get; }
            public bool IsDirectory => false;

            public Stream CreateReadStream() => _stream;
        }
        #endregion;
    }
}
