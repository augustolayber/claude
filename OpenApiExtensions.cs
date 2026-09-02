using System.Reflection;

public static class OpenApiExtensions
{
    private static string? RemoveSchemaView(Type currentClass)
    {
        // 1. Extrai o tipo base caso seja Nullable (int? vira int)
        var underlyingType = Nullable.GetUnderlyingType(currentClass) ?? currentClass;
        currentClass = underlyingType;

        // 2. FILTRO DE PRIMITIVOS E ESTRUTURAS BÁSICAS
        // Precisa retornar null: o gerador de OpenAPI do .NET 10 só infere
        // corretamente o "type"/"format" (integer, boolean, string+date-time, uuid...)
        // para esses tipos quando eles são inlinados. Dar um reference id a eles
        // faz o gerador tratá-los como schema reutilizável e o "type" cai para
        // "string" em todos, pois ele não resolve o tipo real de tipos "folha"
        // fora do contexto de uma propriedade. Retornando null aqui, o campo é
        // inlinado com o tipo correto (ex: "type": "integer", "type": "boolean").
        if (currentClass.IsPrimitive ||
            currentClass == typeof(string) ||
            currentClass == typeof(decimal) ||
            currentClass == typeof(DateTime) ||
            currentClass == typeof(DateTimeOffset) ||
            currentClass == typeof(TimeSpan) ||
            currentClass == typeof(Guid))
        {
            return null;
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

    // Mapeia um tipo primitivo/estrutura básica (já sem o wrapper Nullable<T>)
    // para o par (type, format) que o OpenAPI deve exibir.
    private static (JsonSchemaType Type, string? Format)? GetPrimitiveSchemaInfo(Type underlyingType)
    {
        if (underlyingType == typeof(bool))
            return (JsonSchemaType.Boolean, null);

        if (underlyingType == typeof(byte) || underlyingType == typeof(sbyte) ||
            underlyingType == typeof(short) || underlyingType == typeof(ushort) ||
            underlyingType == typeof(int) || underlyingType == typeof(uint))
            return (JsonSchemaType.Integer, "int32");

        if (underlyingType == typeof(long) || underlyingType == typeof(ulong))
            return (JsonSchemaType.Integer, "int64");

        if (underlyingType == typeof(float))
            return (JsonSchemaType.Number, "float");

        if (underlyingType == typeof(double))
            return (JsonSchemaType.Number, "double");

        if (underlyingType == typeof(decimal))
            return (JsonSchemaType.Number, "double");

        if (underlyingType == typeof(char))
            return (JsonSchemaType.String, null);

        if (underlyingType == typeof(string))
            return (JsonSchemaType.String, null);

        if (underlyingType == typeof(Guid))
            return (JsonSchemaType.String, "uuid");

        if (underlyingType == typeof(DateTime))
            return (JsonSchemaType.String, "date-time");

        if (underlyingType == typeof(DateTimeOffset))
            return (JsonSchemaType.String, "date-time");

        if (underlyingType == typeof(TimeSpan))
            return (JsonSchemaType.String, "duration");

        return null;
    }

    // Popula document.Info a partir do DocInfoDto de cada documento configurado.
    private static Func<OpenApiDocument, OpenApiDocumentTransformerContext, CancellationToken, Task> CreateDocumentInfoTransformer(DocInfoDto doc)
    {
        return (document, context, cancellationToken) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = doc.Title,
                Version = doc.Version,
                Description = doc.Description
            };
            return Task.CompletedTask;
        };
    }

    // Ajusta o schema para Enums (exibidos como texto com os valores) e delega
    // o restante (primitivos e nullable) para os métodos auxiliares abaixo.
    private static Task TransformSchema(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;

        // Pega o tipo base (para lidar com tipos nullable de VALUE TYPE, ex: Status?, int?, Guid?)
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        // Nullable<T> cobre value types. Para REFERENCE TYPES (ex: string? Texto),
        // o "?" é só uma anotação de nullable reference type (NRT): o Type continua
        // sendo `string`, então precisamos ler a anotação via reflection.
        var isNullable = underlyingType != type || IsNullableReferenceType(context);

        if (underlyingType.IsEnum)
        {
            ApplyEnumSchema(schema, underlyingType);
        }
        else
        {
            ApplyPrimitiveSchema(schema, underlyingType);
        }

        // Marca o schema como nullable (T?) quando o tipo original era Nullable<T>
        if (isNullable && schema.Type.HasValue)
        {
            schema.Type |= JsonSchemaType.Null;
        }

        return Task.CompletedTask;
    }

    // Detecta "string? Texto", "MinhaClasse? Objeto" etc: reference types onde o
    // "?" não gera Nullable<T>, apenas uma anotação de nullable reference type
    // lida via NullabilityInfoContext a partir do PropertyInfo/FieldInfo original.
    private static bool IsNullableReferenceType(OpenApiSchemaTransformerContext context)
    {
        var attributeProvider = context.JsonPropertyInfo?.AttributeProvider;

        var nullabilityInfo = attributeProvider switch
        {
            PropertyInfo propertyInfo => new NullabilityInfoContext().Create(propertyInfo),
            FieldInfo fieldInfo => new NullabilityInfoContext().Create(fieldInfo),
            _ => null
        };

        return nullabilityInfo?.WriteState == NullabilityState.Nullable;
    }

    private static void ApplyEnumSchema(OpenApiSchema schema, Type enumType)
    {
        // Garante que o tipo no Swagger seja texto
        schema.Type = JsonSchemaType.String;

        // Extrai os nomes do Enum
        var enumNames = Enum.GetNames(enumType);

        // ATUALIZAÇÃO .NET 10: schema.Enum agora recebe IList<JsonNode>
        schema.Enum = enumNames
            .Select(name => (JsonNode)JsonValue.Create(name))
            .ToList();
    }

    private static void ApplyPrimitiveSchema(OpenApiSchema schema, Type underlyingType)
    {
        // Preenche explicitamente type/format dos primitivos e estruturas básicas
        // (int, bool, Guid, DateTime, ...), pois nem sempre o gerador nativo
        // consegue inferi-los sozinho.
        var primitiveInfo = GetPrimitiveSchemaInfo(underlyingType);
        if (primitiveInfo is { } info)
        {
            schema.Type = info.Type;
            schema.Format = info.Format;
        }
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
                options.AddDocumentTransformer(CreateDocumentInfoTransformer(doc));
                options.AddSchemaTransformer(TransformSchema);
                options.CreateSchemaReferenceId = (typeInfo) => RemoveSchemaView(typeInfo.Type);
                options.AddOpenApiAuthentication();
            });
        }

        return builder;
    }
}
