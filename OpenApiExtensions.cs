public static class OpenApiExtensions
{
    private static string? RemoveSchemaView(Type currentClass)
    {
        // 1. Extrai o tipo base caso seja Nullable (int? vira int)
        var underlyingType = Nullable.GetUnderlyingType(currentClass) ?? currentClass;
        currentClass = underlyingType;

        // 2. TIPOS PRIMITIVOS E ESTRUTURAS BÁSICAS
        // Recebem o próprio nome do tipo (ex: "Int32", "String", "Guid") para que o
        // OpenAPI gere um schema referenciável ($ref) também para eles, em vez de
        // inlinar o campo sem criar entrada em "Schemas".
        if (currentClass.IsPrimitive ||
            currentClass == typeof(string) ||
            currentClass == typeof(decimal) ||
            currentClass == typeof(DateTime) ||
            currentClass == typeof(DateTimeOffset) ||
            currentClass == typeof(TimeSpan) ||
            currentClass == typeof(Guid))
        {
            return currentClass.Name;
        }

        // 3. FILTRO DE LISTAS E ARRAYS NATOS
        // Ignora List<T>, IEnumerable<T> e arrays, deixando o OpenAPI montar o array inline
        if (currentClass.IsArray ||
           (currentClass.Namespace != null && currentClass.Namespace.StartsWith("System.Collections")))
        {
            return null;
        }

        // 4. TRATAMENTO PARA GENÉRICOS CUSTOMIZADOS (ex: Envelope<T>, Paginacao<T>)
        if (currentClass.IsGenericType)
        {
            string genericTypeName = currentClass.Name.Split('`')[0];

            var genericArguments = currentClass.GetGenericArguments()
                .Select(t => RemoveSchemaView(t) ?? t.Name); // Fallback: usa o nome base se o T for primitivo (ex: PaginacaoOfString)

            return $"{genericTypeName}Of{string.Join("And", genericArguments)}";
        }

        // 5. TRATAMENTO PARA CLASSES NORMAIS (Remove Dto/Model)
        string returnedValue = currentClass.Name;

        if (returnedValue.EndsWith("Dto") && returnedValue.Length > 3)
        {
            returnedValue = returnedValue.Substring(0, returnedValue.Length - 3);
        }
        else if (returnedValue.EndsWith("Model") && returnedValue.Length > 5)
        {
            returnedValue = returnedValue.Substring(0, returnedValue.Length - 5);
        }

        return returnedValue;
    }

    // =========================================================================
    // ABORDAGEM NOVA: .NET 10 Nativo (AddOpenApi)
    // =========================================================================

    public static WebApplicationBuilder AddOpenApiDoc(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var docs = configuration.GetSection("SwaggerDocs").Get<IList<DocInfoDto>>() ?? new List<DocInfoDto>();

        foreach (var doc in docs)
        {
            builder.Services.AddOpenApi(doc.Guid!, options =>
            {
                options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0;

                options.AddDocumentTransformer((document, context, cancellationToken) =>
                {
                    document.Info = new OpenApiInfo
                    {
                        Title = doc.Title,
                        Version = doc.Version,
                        Description = doc.Description
                    };
                    return Task.CompletedTask;
                });

                // =======================================================================
                // NOVO: Transformer para garantir que os valores dos Enums apareçam
                // =======================================================================
                options.AddSchemaTransformer((schema, context, cancellationToken) =>
                {
                    var type = context.JsonTypeInfo.Type;

                    // Pega o tipo base (para lidar com Enums nullable, ex: Status?)
                    var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

                    if (underlyingType.IsEnum)
                    {
                        // Garante que o tipo no Swagger seja texto
                        schema.Type = JsonSchemaType.String;

                        // Extrai os nomes do Enum
                        var enumNames = Enum.GetNames(underlyingType);

                        // ATUALIZAÇÃO .NET 10: schema.Enum agora recebe IList<JsonNode>
                        schema.Enum = enumNames
                            .Select(name => (JsonNode)JsonValue.Create(name))
                            .ToList();
                    }

                    return Task.CompletedTask;
                });
                // =======================================================================

                options.CreateSchemaReferenceId = (typeInfo) => RemoveSchemaView(typeInfo.Type);

                options.AddOpenApiAuthentication();
            });
        }

        return builder;
    }
}
