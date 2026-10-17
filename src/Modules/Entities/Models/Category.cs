using MongoDB.Bson.Serialization.Attributes;
using Ratable.Shared.Data;

namespace Ratable.Modules.Entities.Models;

public enum AnswerType { Rating5, Choice, YesNo }

/// <summary>A category is configuration (CLAUDE.md rule 8): adding one means adding a document, not code.</summary>
public sealed class Category
{
    /// <summary>The slug, for example "compounds". Stable, used in URLs.</summary>
    [BsonId] public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public string Icon { get; set; } = "";
    /// <summary>schema.org type used in JSON-LD: Hotel, Restaurant, School, Product, LocalBusiness.</summary>
    public string SchemaOrgType { get; set; } = "LocalBusiness";
    public List<Criterion> Criteria { get; set; } = [];
    public List<string> AcceptedProofTypes { get; set; } = [];
    public bool AnonymousByDefault { get; set; }
    public List<EntityFieldDefinition> EntityFields { get; set; } = [];
    public bool VerdictEnabled { get; set; } = true;
    public bool CollectionsEnabled { get; set; } = true;
    /// <summary>Rule 19: when true, profiles in this category hide contact details until the owner's plan includes contact. Off by default.</summary>
    public bool ContactGated { get; set; }
    /// <summary>Rule 18: true when the category rates individuals (service providers), so their reviews always go to a moderator first. Off by default.</summary>
    public bool RatesIndividuals { get; set; }
    public bool Active { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Criterion
{
    /// <summary>Stored as "id". The attribute stops the driver treating a property named Id as a document _id.</summary>
    [BsonElement("id")] public string Id { get; set; } = "";
    public LocalizedText Label { get; set; } = new();
    public AnswerType AnswerType { get; set; } = AnswerType.Rating5;
    /// <summary>Choices (and Yes/No). Each maps to a 1 to 5 score so the criterion can be averaged.</summary>
    public List<CriterionOption> Options { get; set; } = [];
}

public sealed class CriterionOption
{
    public string Value { get; set; } = "";
    public LocalizedText Label { get; set; } = new();
    public int Score { get; set; }
}

public sealed class EntityFieldDefinition
{
    public string Key { get; set; } = "";
    public LocalizedText Label { get; set; } = new();
    /// <summary>text, number or choice.</summary>
    public string Type { get; set; } = "text";
    public List<CriterionOption> Options { get; set; } = [];
}
