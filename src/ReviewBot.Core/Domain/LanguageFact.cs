namespace ReviewBot.Core.Domain;

/// <summary>
/// Something about a construct in the diff that the compiler settles outright, stated in
/// the prompt so the model does not have to guess at it.
/// </summary>
/// <remarks>
/// The verification tier refutes a wrong claim after the model has made it, and only when
/// a classifier recognises the English the model happened to use — a model that writes
/// "preserve" where the classifier expects "retains" sails through. A fact supplied up
/// front needs no classifier and cannot be phrased around: the model is told the answer
/// before it forms an opinion. Cheaper than a refutation and it fails safe, since an
/// unstated fact simply leaves current behaviour unchanged.
/// </remarks>
public sealed record LanguageFact(string Path, int Line, string Fact);
