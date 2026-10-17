using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;

namespace Ratable.Shared.Data;

public static class MongoConventions
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>camelCase fields, enums stored as strings, unknown fields ignored. Safe to call many times.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;
            ConventionRegistry.Register("ratable", new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new EnumRepresentationConvention(BsonType.String),
                new IgnoreExtraElementsConvention(true),
                // Unset fields are left out of the document, so sparse and partial indexes (geo, unique ids) skip them.
                new IgnoreIfNullConvention(true),
            }, _ => true);
            _registered = true;
        }
    }
}
