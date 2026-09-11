namespace Infrastructure.Persistence.Inventory;

/// <summary>
/// The written-down half of the data inventory: what the model is allowed not to describe.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about the inventory is discovered — the columns from
/// <see cref="MappedSchema" />, the relations from the live catalog — because a written-down subject
/// fails open: the relation nobody adds to it is the relation no rule is ever applied to. What has
/// to be written down is the exception, which fails closed in the same way
/// <see cref="Provisioning.RowLevelSecurityCoverage.Exemptions" /> does: a relation leaves the
/// reconciliation only by being named here, with a reason, and anything that appears without being
/// named stays red until somebody decides about it.
/// </para>
/// <para>
/// It lives in production code rather than as a constant in the test that reads it today. The
/// inventory is on its way to being a build gate, and a gate cannot reference a test assembly —
/// which is the call already made for <c>ProhibitedColumnVocabulary</c>. Putting the list here now
/// means the later story that tightens "no coverage test names a table" has nothing to relocate.
/// </para>
/// </remarks>
public static class DataInventory
{
    /// <summary>
    /// The relations that exist in the database and that the EF model deliberately does not map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An entry excuses the relation, never its columns.</b> That is the shape of the decision
    /// rather than a convenience: a relation is outside the model or it is not, and there is no
    /// coherent middle where a mapped table drops one of its columns out of the inventory. A
    /// per-column list would offer exactly that middle, and the column it hid would be hidden under a
    /// reason written about the table. <see cref="Provisioning.TableExemption" /> draws the same line
    /// from the other side: it pins the column set an exemption was argued over, and pins
    /// <c>null</c> for this same relation on the ground that EF owns its shape, so there is nothing
    /// about that shape for anyone here to have an opinion on.
    /// </para>
    /// <para>
    /// <c>__EFMigrationsHistory</c> is EF's own bookkeeping. EF creates it, EF decides what it holds,
    /// and the model does not map it because mapping it would be this application claiming ownership
    /// of a table it does not get to change. So it is not model drift and never will be — it is a
    /// relation the model does not describe and never should, which is the only kind of thing this
    /// list may hold. A relation that appears here for any other reason is a configuration somebody
    /// has not written yet.
    /// </para>
    /// <para>
    /// The name is spelled exactly as <c>pg_class</c> stores it, quoting and all. Every comparison
    /// against it is ordinal for that reason: EF quotes the identifier, so PostgreSQL keeps the
    /// capitals, and a loose comparison would let an entry claim a relation it does not name.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> RelationsOutsideTheModel { get; } = ["__EFMigrationsHistory"];

    /// <summary>
    /// Every column the model maps, with what the product owes the person for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The three words classify a column by the obligation it carries, not by what it holds.</b>
    /// <see cref="ColumnClassification.Narrative" /> is user-authored free text — ciphertext in the
    /// column, absent from every log, present in the export.
    /// <see cref="ColumnClassification.Arithmetic" /> is server-readable and part of what the person
    /// owns, so the export is a copy of it. <see cref="ColumnClassification.Excluded" /> is everything
    /// the export deliberately does not carry, each with a written reason. The SRS Definitions table
    /// describes a sensitivity split <i>inside a budget</i> and is not a rule for classifying
    /// <c>session_tokens.token_hash</c>.
    /// </para>
    /// <para>
    /// <b>Narrative is measured rather than chosen.</b> Exactly eight properties report
    /// <c>ClrType == typeof(NarrativeField)</c>, and
    /// <c>DataInventoryCoverageTests.Narrative_IsExactlyTheColumnsTypedForASealedValue</c> reads that
    /// set off the model, so the eight entries below can only agree with the model or go red. A ninth
    /// sealed column, or one of these eight losing its type, reddens without anybody editing this
    /// list.
    /// </para>
    /// <para>
    /// <b>Narrative and arithmetic together are the members of
    /// <c>Application.Users.ExportData.ExportDocument</c>, one for one.</b> Forty-one columns carry
    /// one of those two words and the document's seven records declare forty-one members between
    /// them; ten owned-table columns are excluded — the four <c>name_key</c> blind indexes and the six
    /// <c>rotation_id</c> stamps — and each record's own remarks already argue for leaving its own
    /// out. The stamps joined that set without moving the number above it, which is the property to
    /// check when one of these tables grows a column: a new owned-table column is either a
    /// forty-second member of the document or an eleventh written exclusion, and never neither. That
    /// correspondence is the whole
    /// content of "the export is a copy of what the person owns", and it is what a later card asserts
    /// — so a column moved between these two words and the export is a column the two files now
    /// disagree about.
    /// </para>
    /// <para>
    /// <b>Two readings a reader will get wrong, both argued at the entry rather than here.</b>
    /// <c>credentials.created_at_utc</c> is an ordinary timestamp and is excluded, because a
    /// credential is not content a person owns and its creation instant is sign-in history: the type
    /// of a column decides nothing. <c>users.email</c> is arithmetic and <i>is</i> exported, even
    /// though a separate requirement forbids it from a log record — that prohibition rides its own
    /// named list, and "not in a log" and "not in the export" are different obligations.
    /// </para>
    /// <para>
    /// <b>One entry per column, and never a reason written at table grain for its columns to
    /// inherit.</b> Sixty-four written reasons is a real cost and the obvious saving is the wrong one:
    /// a reason argued about a table drifts the moment a column arrives that it was not about, which
    /// is exactly why <see cref="Provisioning.TableExemption" /> had to grow
    /// <see cref="Provisioning.TableExemption.ColumnsTheReasonCovers" />, and why the rule there is
    /// <i>move the column, do not widen the pin</i>. The ten wholly-excluded tables below are ten
    /// tables' worth of separate arguments, not ten sentences and a rubber stamp — and the six
    /// <c>rotation_id</c> stamps are six more, spread across six owned tables that each lose
    /// something different by publishing one.
    /// </para>
    /// <para>
    /// <b>The reasons are the review surface no test can judge.</b> A length floor is asserted next
    /// door; all it can do is make writing nothing impossible. What each sentence has to earn is a
    /// reader's disagreement — say what the person loses by the column's absence and why that is
    /// right — and a sentence that only restates the verdict has passed the floor and failed the
    /// requirement.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ColumnClassificationEntry> Entries { get; } =
    [
        // users — the account itself. Every column ships; ExportedUser carries all three.
        ColumnClassificationEntry.Arithmetic("users", "id"),
        ColumnClassificationEntry.Arithmetic("users", "email"),
        ColumnClassificationEntry.Arithmetic("users", "created_at_utc"),

        // budgets — the unit of tenancy. Its name is sealed and carries no blind index.
        ColumnClassificationEntry.Arithmetic("budgets", "id"),
        ColumnClassificationEntry.Arithmetic("budgets", "user_id"),
        ColumnClassificationEntry.Narrative("budgets", "name"),
        ColumnClassificationEntry.Arithmetic("budgets", "base_currency_code"),
        ColumnClassificationEntry.Excluded(
            "budgets",
            "rotation_id",
            "names the content-key rotation that last re-sealed this row, and it is the first of six "
            + "identical stamps — argued separately on each table, because the six say different "
            + "things about what a reader would lose. Here it would be a foreign key into "
            + "key_rotations, a table whose row is deleted the moment the run it describes finishes, "
            + "so a durable file naming one names something that no longer exists by the time "
            + "anybody opens the file. The person is owed the budget's name, which ships sealed one "
            + "column over; which generation of key sealed it is the server's bookkeeping about work "
            + "it did on their behalf"),
        ColumnClassificationEntry.Arithmetic("budgets", "created_at_utc"),

        // accounts
        ColumnClassificationEntry.Arithmetic("accounts", "id"),
        ColumnClassificationEntry.Arithmetic("accounts", "budget_id"),
        ColumnClassificationEntry.Narrative("accounts", "name"),
        ColumnClassificationEntry.Excluded(
            "accounts",
            "name_key",
            "the blind index over an account's name — derivable from the name by anybody holding the "
            + "account's index key, which is exactly who can read this file, and meaningless to "
            + "anybody who is not. Shipping it would add a deterministic per-budget fingerprint of "
            + "every account name while handing the person back nothing they cannot recompute from "
            + "the name the export already carries"),
        ColumnClassificationEntry.Arithmetic("accounts", "type"),
        ColumnClassificationEntry.Arithmetic("accounts", "opening_balance"),
        ColumnClassificationEntry.Arithmetic("accounts", "currency_code"),
        ColumnClassificationEntry.Excluded(
            "accounts",
            "rotation_id",
            "the rotation stamp on the table that carries a blind index beside its sealed name. A "
            + "rotation changes the index key as well as the content key, so this row's name_key is "
            + "re-derived whenever this stamp moves — and name_key is itself excluded, so publishing "
            + "the stamp would hand a reader a marker for the regeneration of a column the file "
            + "deliberately does not carry. The name the person actually owns ships sealed, with "
            + "nothing about which generation sealed it left to work out"),
        ColumnClassificationEntry.Arithmetic("accounts", "created_at_utc"),

        // category_groups — the first table carrying two sealed columns, name and description.
        ColumnClassificationEntry.Arithmetic("category_groups", "id"),
        ColumnClassificationEntry.Arithmetic("category_groups", "budget_id"),
        ColumnClassificationEntry.Narrative("category_groups", "name"),
        ColumnClassificationEntry.Excluded(
            "category_groups",
            "name_key",
            "the blind index over a group's name. Group names are few and drawn from a small, "
            + "guessable vocabulary, which makes this the fingerprint of the four closest to being "
            + "invertible by a dictionary — and the person can recompute it from the name the export "
            + "does carry, so publishing it is all risk and no return"),
        ColumnClassificationEntry.Narrative("category_groups", "description"),
        ColumnClassificationEntry.Arithmetic("category_groups", "position"),
        ColumnClassificationEntry.Excluded(
            "category_groups",
            "rotation_id",
            "the rotation stamp on the first table holding two sealed columns. One stamp covers the "
            + "row, so in a file it would say 'both of these were rewritten together' about a name "
            + "and a description the export already carries in full — a statement about the order "
            + "work happened in rather than about the group. A person reading their own export wants "
            + "the group and its note; how many passes the server made over them answers nothing "
            + "they asked"),
        ColumnClassificationEntry.Arithmetic("category_groups", "created_at_utc"),

        // categories
        ColumnClassificationEntry.Arithmetic("categories", "id"),
        ColumnClassificationEntry.Arithmetic("categories", "budget_id"),
        ColumnClassificationEntry.Arithmetic("categories", "category_group_id"),
        ColumnClassificationEntry.Narrative("categories", "name"),
        ColumnClassificationEntry.Excluded(
            "categories",
            "name_key",
            "the blind index over a category's name. Category names are the most predictable text in "
            + "the product, which makes a deterministic digest of them the easiest of the four to "
            + "attack by guessing; the person loses nothing by its absence, because the index is "
            + "derivable from the name this file already carries"),
        ColumnClassificationEntry.Narrative("categories", "description"),
        ColumnClassificationEntry.Arithmetic("categories", "position"),
        ColumnClassificationEntry.Excluded(
            "categories",
            "rotation_id",
            "the rotation stamp on the rows a person has the most of after transactions, and the "
            + "place where publishing it would be closest to useful and still wrong. Read across a "
            + "whole budget the six stamps reconstruct the order and the shape of an interrupted "
            + "run — which rows were reached before somebody closed the tab — which is a trace of "
            + "how the account's key custody was maintained rather than anything about a category. "
            + "The category and its note ship; the maintenance record does not"),
        ColumnClassificationEntry.Arithmetic("categories", "created_at_utc"),

        // payees
        ColumnClassificationEntry.Arithmetic("payees", "id"),
        ColumnClassificationEntry.Arithmetic("payees", "budget_id"),
        ColumnClassificationEntry.Narrative("payees", "name"),
        ColumnClassificationEntry.Excluded(
            "payees",
            "name_key",
            "the blind index over a payee's name, and the most telling of the four: a payee list is "
            + "the set of counterparties one person deals with, so a deterministic digest of it lets "
            + "a reader confirm guesses at who they pay. It is derivable from the name the export "
            + "carries, so its absence costs the person nothing at all"),
        ColumnClassificationEntry.Excluded(
            "payees",
            "rotation_id",
            "the rotation stamp on the table whose blind index is load-bearing rather than tidy: "
            + "name_key is the whole of counterparty deduplication here, and a rotation re-derives "
            + "every value in it. So this stamp is the marker of the riskiest rewrite in the "
            + "product, and a file naming it would invite a reader to believe the payee list could "
            + "be reconstructed or repaired from the export. It cannot — the names ship sealed and "
            + "the indexes ship not at all"),
        ColumnClassificationEntry.Arithmetic("payees", "created_at_utc"),

        // transactions — the leaf, and the one owned table that omits nothing.
        ColumnClassificationEntry.Arithmetic("transactions", "id"),
        ColumnClassificationEntry.Arithmetic("transactions", "budget_id"),
        ColumnClassificationEntry.Arithmetic("transactions", "account_id"),
        ColumnClassificationEntry.Arithmetic("transactions", "amount"),
        ColumnClassificationEntry.Arithmetic("transactions", "date"),
        ColumnClassificationEntry.Narrative("transactions", "description"),
        ColumnClassificationEntry.Arithmetic("transactions", "payee_id"),
        ColumnClassificationEntry.Arithmetic("transactions", "category_id"),
        ColumnClassificationEntry.Excluded(
            "transactions",
            "rotation_id",
            "the rotation stamp on the highest-volume table in an account, which is what makes it "
            + "the costly one to publish and the least informative. Repeated on every row, it would "
            + "add a column to the largest part of the file to say the same thing each time: that "
            + "the server re-sealed this row during some run. The description the person wrote ships "
            + "sealed and the amount, date, payee and category ship in full — nothing about what "
            + "they recorded depends on knowing which pass rewrote it"),
        ColumnClassificationEntry.Arithmetic("transactions", "created_at_utc"),

        // currencies — shared reference data belonging to no tenant.
        ColumnClassificationEntry.Excluded(
            "currencies",
            "code",
            "an ISO 4217 code from a published standard, identical in every account there will ever "
            + "be. The export is a copy of what this person owns, and reference data they neither "
            + "authored nor can change is not that; the codes their own rows carry are what a reader "
            + "needs to interpret an amount, and those ship on the rows themselves"),
        ColumnClassificationEntry.Excluded(
            "currencies",
            "name",
            "the English display name of a published currency, and the one column called 'name' in "
            + "this schema that nobody authored. A reader holding the ISO 4217 code the exported rows "
            + "carry can look it up in the standard, so copying it here would grow the file by a "
            + "reference table and tell the person nothing about themselves"),
        ColumnClassificationEntry.Excluded(
            "currencies",
            "symbol",
            "the glyph an amount renders with, which is presentation this application supplies rather "
            + "than anything the person recorded. A reader formatting the exported amounts picks its "
            + "own, and copying ours would freeze one product's rendering choice into a file that is "
            + "supposed to outlive the product"),
        ColumnClassificationEntry.Excluded(
            "currencies",
            "minor_unit",
            "how many decimal places a currency subdivides into — a property of the currency rather "
            + "than of anything the person did, published in the same standard as the code and "
            + "already implied by the scale every exported amount is written at"),

        // credentials — how somebody signs in, which is not content they own.
        ColumnClassificationEntry.Excluded(
            "credentials",
            "id",
            "the surrogate key of a sign-in factor. The export copies content, and a credential is "
            + "the means of reaching content rather than content itself; the id opens nothing on its "
            + "own, and returning it would invite a reader to believe a factor can be restored from "
            + "the file, which no route in this product can do"),
        ColumnClassificationEntry.Excluded(
            "credentials",
            "user_id",
            "names the account on a row about how somebody signs in. The account is already named by "
            + "the exported user, so this repeats an identifier the file carries while attaching it "
            + "to front-door machinery the person does not own and could not restore from a copy"),
        ColumnClassificationEntry.Excluded(
            "credentials",
            "type",
            "which kinds of factor the account holds — passkey, federated, recovery codes — which is "
            + "the shape of somebody's security setup. The settings screen shows it while a live "
            + "session vouches for who is asking; a copy in a file that outlives every session turns "
            + "one careless download into a map of how to attack the account"),
        ColumnClassificationEntry.Excluded(
            "credentials",
            "provider",
            "names the identity provider that gated registration, which is a fact about this "
            + "product's front door rather than about the person's money. It also records a "
            + "relationship with a third party, and re-publishing that in a file the person may share "
            + "gives away a link the product otherwise keeps to itself"),
        ColumnClassificationEntry.Excluded(
            "credentials",
            "subject",
            "the provider's opaque identifier for the person, and the strongest cross-service "
            + "correlator in the schema: anybody holding it can tie this account to every other "
            + "service the same provider account signs into. No response body in the product carries "
            + "it, and a file people mail to themselves is the last place it should appear"),
        ColumnClassificationEntry.Excluded(
            "credentials",
            "created_at_utc",
            "the instant an authenticator was enrolled, and the entry a reader will want to move: it "
            + "is an ordinary timestamp, which is exactly why it is worth arguing. A credential is "
            + "not content a person owns, so the moment one was added is sign-in history — the type "
            + "of a column decides nothing about which of the three words it earns"),

        // webauthn_challenges — nonces belonging to a ceremony rather than to a person.
        ColumnClassificationEntry.Excluded(
            "webauthn_challenges",
            "id",
            "the key of a ceremony nonce whose whole life is the few seconds between a screen asking "
            + "for a passkey and the authenticator answering. The row is consumed when the ceremony "
            + "is spent, so it describes nothing the person did and would in any case be gone before "
            + "a file naming it could be written"),
        ColumnClassificationEntry.Excluded(
            "webauthn_challenges",
            "challenge",
            "the thirty-two random bytes an authenticator signs over, which are live security "
            + "material for as long as the ceremony is open. Publishing one invites a replay, and it "
            + "says nothing about the person in any case: the authentication pool mints its nonces "
            + "before anybody has said who they are"),
        ColumnClassificationEntry.Excluded(
            "webauthn_challenges",
            "ceremony",
            "which of the four pools a nonce was issued into — registration, authentication, "
            + "reauthentication, account registration — which is an implementation detail of this "
            + "product's front door. It describes the protocol rather than the person, and would tell "
            + "a reader only how the sign-in screens happen to be wired today"),
        ColumnClassificationEntry.Excluded(
            "webauthn_challenges",
            "created_at_utc",
            "when a ceremony began, which is sign-in history rather than content — and on this table "
            + "it is history about a ceremony no row ties to a person at all, so the export could not "
            + "file it under the right account even if it wanted to"),
        ColumnClassificationEntry.Excluded(
            "webauthn_challenges",
            "expires_at_utc",
            "when a nonce stops being spendable: a lifetime measured in seconds that the server "
            + "enforces and the person never sees. It is bookkeeping for a value that is deliberately "
            + "short-lived, so a copy in a durable file describes a moment already long past by the "
            + "time anybody reads it"),

        // passkey_public_keys — what verifies a signature before the request has an identity.
        ColumnClassificationEntry.Excluded(
            "passkey_public_keys",
            "credential_id",
            "names the passkey a public key belongs to. Everything on this table is the machinery by "
            + "which a request proves who is asking, and an identifier into machinery restores "
            + "nothing: no route in this product enrols a passkey from a file, so the person could do "
            + "nothing with it but read it"),
        ColumnClassificationEntry.Excluded(
            "passkey_public_keys",
            "user_id",
            "repeats the account already named by the exported user, here on the row that exists so "
            + "an assertion's signature can be checked before anybody knows whose it is. That row is "
            + "the front door's own state; the person owns what is behind the door rather than the "
            + "lock"),
        ColumnClassificationEntry.Excluded(
            "passkey_public_keys",
            "credential_type",
            "records that this row's credential is a passkey, which the table's own CHECK constraint "
            + "already fixes to a single value. It exists so the composite foreign key can reach "
            + "credentials, and a copy of the schema's referential machinery is not among the things "
            + "a person is owed"),
        ColumnClassificationEntry.Excluded(
            "passkey_public_keys",
            "webauthn_credential_id",
            "the handle an authenticator hands back on every assertion, which is a durable identifier "
            + "for the security key or platform authenticator the person signs in with. A reader of "
            + "the file could correlate it wherever else that authenticator is used, and could "
            + "re-register it nowhere"),
        ColumnClassificationEntry.Excluded(
            "passkey_public_keys",
            "public_key_cose",
            "the public half of a passkey, and public is not the same as owed. The private half never "
            + "left the authenticator, so nothing can be re-enrolled from this value; it gives the "
            + "person nothing back while giving anybody reading the file a stable fingerprint of the "
            + "device they sign in with"),
        ColumnClassificationEntry.Excluded(
            "passkey_public_keys",
            "cose_algorithm",
            "which signature algorithm the authenticator negotiated, which is a property of the "
            + "hardware rather than of the person. It narrows what kind of device they carry and "
            + "answers no question anybody could ask about their own money"),

        // passkey_signature_counters — a clone check the server runs on the person's behalf.
        ColumnClassificationEntry.Excluded(
            "passkey_signature_counters",
            "credential_id",
            "names the passkey whose signature counter this row keeps. The counter exists to notice a "
            + "cloned authenticator, so this table is a fraud control rather than content; the person "
            + "owns what the control protects, and an identifier into the control restores none of "
            + "it"),
        ColumnClassificationEntry.Excluded(
            "passkey_signature_counters",
            "user_id",
            "repeats the account already named by the exported user, on a row that exists only so a "
            + "clone check can be filed against the right authenticator. Nothing the person recorded "
            + "is stored here, and nothing here could be restored from a copy"),
        ColumnClassificationEntry.Excluded(
            "passkey_signature_counters",
            "credential_type",
            "records that the counter belongs to a passkey, which the table's own CHECK constraint "
            + "pins to a single value. It exists so the composite foreign key can reach credentials, "
            + "and the export is not a copy of how this schema wires its references together"),
        ColumnClassificationEntry.Excluded(
            "passkey_signature_counters",
            "signature_counter",
            "how many times an authenticator has signed, which is a count of sign-ins and therefore "
            + "sign-in history. It is also meaningless away from the live row it is compared against: "
            + "the number only ever answers 'is this higher than last time', and a file has no last "
            + "time to compare with"),

        // recovery_code_hashes — the digests of a secret this server has never held.
        ColumnClassificationEntry.Excluded(
            "recovery_code_hashes",
            "verifier_hash",
            "the SHA-256 of a verifier derived from a recovery code that never reached this server. "
            + "Putting it in a file hands an offline guessing target for the one secret that unwraps "
            + "the account's keys, and returns nothing usable: what a person needs is the code "
            + "itself, which this server has never held and cannot give back"),
        ColumnClassificationEntry.Excluded(
            "recovery_code_hashes",
            "credential_id",
            "names the set a code belongs to, on rows whose whole purpose is to recognise a secret "
            + "the server cannot read. Recovery codes are handed over once, on screen, at "
            + "registration; an identifier into the hashes is not a copy of them and would let "
            + "nobody recover anything"),
        ColumnClassificationEntry.Excluded(
            "recovery_code_hashes",
            "user_id",
            "repeats the account already named by the exported user, here on a row found by hash "
            + "before anybody has said who they are. The row is the anonymous half of the redemption "
            + "path — front-door machinery — and the person owns what redemption reaches rather than "
            + "the lookup that reaches it"),
        ColumnClassificationEntry.Excluded(
            "recovery_code_hashes",
            "credential_type",
            "records that the row belongs to a recovery-code set, which the table's own CHECK "
            + "constraint pins to a single value. It exists so the composite foreign key can reach "
            + "credentials, and referential machinery is not among the things a person is owed a copy "
            + "of"),
        ColumnClassificationEntry.Excluded(
            "recovery_code_hashes",
            "created_at_utc",
            "when a set of codes was minted, which dates the last time somebody rotated their "
            + "recovery factors. That is security history rather than content, and a reader of the "
            + "file would learn from it only how stale the codes on that person's card are likely to "
            + "be"),

        // sessions — a record of somebody signing in, which is history rather than content.
        ColumnClassificationEntry.Excluded(
            "sessions",
            "id",
            "names one browser session. No response body in this product carries a session id, which "
            + "a census over every type a route returns holds; a downloadable file naming one would "
            + "undo that decision in the single artefact people forward, upload and leave on shared "
            + "machines"),
        ColumnClassificationEntry.Excluded(
            "sessions",
            "user_id",
            "repeats the account already named by the exported user, on a row recording that somebody "
            + "signed in. The repetition is not the objection — the row is: a session is an event in "
            + "the person's sign-in history rather than anything they authored"),
        ColumnClassificationEntry.Excluded(
            "sessions",
            "credential_id",
            "which factor opened a session, and the column a later revocation sweep turns on. It "
            + "records how somebody got in rather than what they did once inside, and read beside the "
            + "credential list it would tell a reader which authenticator is in daily use and which "
            + "is the spare"),
        ColumnClassificationEntry.Excluded(
            "sessions",
            "credential_type",
            "whether a session was opened by a passkey, a recovery code or the provider. It describes "
            + "the strength of a past sign-in — security history — and answers nothing anybody could "
            + "ask about their own budget"),
        ColumnClassificationEntry.Excluded(
            "sessions",
            "kind",
            "whether a session reaches budget content or is locked to a single route. That is an "
            + "authorization state this product computes and enforces rather than anything the person "
            + "set, and it stops being true of anything the moment the session ends"),
        ColumnClassificationEntry.Excluded(
            "sessions",
            "created_at_utc",
            "when somebody signed in. A list of sign-in times is the clearest case in this schema of a "
            + "fact about a person that is not content they own, and it is exactly the pattern-of-life "
            + "detail an export should not be quietly accumulating on their behalf"),
        ColumnClassificationEntry.Excluded(
            "sessions",
            "expires_at_utc",
            "when a session was due to lapse, which is a deadline the server set rather than anything "
            + "the person chose. Once the session is over the value describes nothing that can still "
            + "happen, and while it is live it belongs to the request rather than to a file"),
        ColumnClassificationEntry.Excluded(
            "sessions",
            "revoked_at_utc",
            "when a session was cut short, where one was. It records that somebody signed out or that "
            + "a sweep ended their session, which would let a reader of the file reconstruct when an "
            + "account was thought to be at risk — security history of the most sensitive kind"),

        // session_tokens — read before a request has any identity, and one preimage from a session.
        ColumnClassificationEntry.Excluded(
            "session_tokens",
            "token_hash",
            "the SHA-256 of the cookie value that authenticates every request in the product. The "
            + "digest is what a request is matched by, so a file holding it holds an offline target "
            + "one preimage away from a live session — and the person gains nothing, because a "
            + "session is not something anybody restores from a file"),
        ColumnClassificationEntry.Excluded(
            "session_tokens",
            "session_id",
            "names the session a token stands for, on the one table read before a request has any "
            + "identity at all. It is front-door machinery, and the same reasoning that keeps session "
            + "ids out of every response body keeps them out of a file that outlives the session "
            + "entirely"),
        ColumnClassificationEntry.Excluded(
            "session_tokens",
            "user_id",
            "repeats the account already named by the exported user, here on the row that answers "
            + "'whose request is this' before the request can say. Publishing the lookup adds nothing "
            + "the file does not already carry and shows a reader the shape of how sessions are "
            + "resolved"),

        // wrapped_account_keys — key custody, served by a route gated on presenting a factor.
        ColumnClassificationEntry.Excluded(
            "wrapped_account_keys",
            "factor_id",
            "the client-minted identifier a factor's two envelopes were sealed against — associated "
            + "data rather than content. It opens nothing on its own and is meaningless without the "
            + "envelopes it binds, and those are excluded too, so it would arrive as a bare "
            + "identifier for something the file deliberately does not carry"),
        ColumnClassificationEntry.Excluded(
            "wrapped_account_keys",
            "credential_id",
            "names the credential a factor's envelopes hang off. Key custody is how the person "
            + "reaches their content rather than the content itself, and a browser that presents the "
            + "factor is already handed these rows by GET /api/me/account-keys, which is the route "
            + "designed to give them up"),
        ColumnClassificationEntry.Excluded(
            "wrapped_account_keys",
            "user_id",
            "repeats the account already named by the exported user, on a row about key custody. "
            + "Custody is the mechanism by which the export's sealed columns become readable, it is "
            + "served by a route gated on presenting a factor, and a static file is the wrong shape "
            + "for a mechanism that has to be re-earned each time"),
        ColumnClassificationEntry.Excluded(
            "wrapped_account_keys",
            "credential_type",
            "whether the factor is a passkey or one of ten recovery codes, which the table's own "
            + "CHECK constraint already narrows. It describes the shape of the account's key custody "
            + "— security setup rather than content — and its consumer is a browser mid-ceremony, "
            + "never a reader of a file"),
        ColumnClassificationEntry.Excluded(
            "wrapped_account_keys",
            "wrapped_content_key",
            "the account's content key sealed under one factor's key-encryption key. Only that factor "
            + "opens it, and a browser holding the factor is handed the envelope over a live session "
            + "by GET /api/me/account-keys — so putting every factor's copy in a durable file adds an "
            + "offline target against the weakest of them and gives the person nothing the route does "
            + "not already give"),
        ColumnClassificationEntry.Excluded(
            "wrapped_account_keys",
            "wrapped_index_key",
            "the account's index key sealed the same way, and the more damaging of the pair to "
            + "publish: opening it regenerates every blind index in the budget, which turns a stolen "
            + "export from a pile of ciphertext into an oracle that confirms guesses at names. The "
            + "factor that opens it is what the person holds, and the route that serves it is where "
            + "it belongs"),
        ColumnClassificationEntry.Excluded(
            "wrapped_account_keys",
            "created_at_utc",
            "when a factor's envelopes were written, which dates when a passkey or a set of recovery "
            + "codes was enrolled. That is key-custody history rather than content, and it would tell "
            + "a reader of the file how long the account has had each way in"),

        // key_rotations — the staging row of an unfinished re-seal, which exists only while one is
        // running. Every column is excluded, and the arguments differ because the columns do.
        ColumnClassificationEntry.Excluded(
            "key_rotations",
            "user_id",
            "repeats the account already named by the exported user, here on the row that says a "
            + "re-seal of the account's own content is part-way through. An export taken while one "
            + "is running would carry a claim about work in progress into a file that outlives the "
            + "work, and the row it names is deleted the moment the run ends — so the copy would be "
            + "stale before anybody read it"),
        ColumnClassificationEntry.Excluded(
            "key_rotations",
            "rotation_id",
            "the identifier a run is continued and completed by. It is the value the six stamps on "
            + "the owned tables quote, and a client presenting it is asking the server to keep "
            + "writing to a run somebody already started. Putting it in a downloadable file hands "
            + "that handle to whoever holds the file; it gives the person nothing back, because the "
            + "way to resume a rotation is the browser that began it, never a value typed in"),
        ColumnClassificationEntry.Excluded(
            "key_rotations",
            "factor_id",
            "the client-minted identifier the staged envelopes were sealed against — associated data "
            + "rather than content, exactly as on wrapped_account_keys. It opens nothing on its own "
            + "and is meaningless without the envelopes it binds, and those are excluded here too, "
            + "so it would arrive as a bare identifier for something the file deliberately does not "
            + "carry"),
        ColumnClassificationEntry.Excluded(
            "key_rotations",
            "wrapped_content_key",
            "the account's NEXT content key, sealed under one factor's key-encryption key and not "
            + "yet in force. It is the same kind of value wrapped_account_keys holds and the same "
            + "answer applies — only the factor opens it, and a browser holding the factor is served "
            + "it over a live session — with one thing on top: while a run is in flight this is the "
            + "only copy of a key half the account's rows are already sealed under, so a file "
            + "holding it is an offline target against material that has no second home"),
        ColumnClassificationEntry.Excluded(
            "key_rotations",
            "wrapped_index_key",
            "the next index key, sealed the same way, and the more damaging of the pair for the "
            + "reason its counterpart next door gives: opening it regenerates every blind index in "
            + "the budget, turning a stolen export from a pile of ciphertext into an oracle that "
            + "confirms guesses at names. That it is the incoming generation rather than the current "
            + "one changes nothing about what opening it would yield"),
        ColumnClassificationEntry.Excluded(
            "key_rotations",
            "started_at_utc",
            "when a re-seal was begun, which dates a security operation rather than anything the "
            + "person recorded. Read beside a file that also carried the stamps it would say how "
            + "long a run has been sitting unfinished — how long the account has been half-way "
            + "between two keys — which is the one fact here an attacker would rather have than the "
            + "person would"),
    ];

    /// <summary>The entries carrying one classification, in the order the list declares them.</summary>
    /// <param name="classification">The word to select.</param>
    /// <returns>Every entry classified that way, which may be none.</returns>
    public static IReadOnlyList<ColumnClassificationEntry> Of(
        ColumnClassification classification) =>
        [.. Entries.Where(entry => entry.Classification == classification)];
}
