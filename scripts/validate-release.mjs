import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const argumentsList = process.argv.slice(2);

function fail(message) {
    throw new Error(message);
}

function read(relativePath) {
    return fs.readFileSync(path.join(root, relativePath), "utf8");
}

function capture(source, expression, label) {
    const match = source.match(expression);
    if (!match)
        fail(`Unable to read ${label}.`);
    return match[1];
}

function ensure(condition, message) {
    if (!condition)
        fail(message);
}

function optionValue(name) {
    const index = argumentsList.indexOf(name);
    if (index < 0)
        return undefined;
    if (!argumentsList[index + 1])
        fail(`${name} requires a value.`);
    return argumentsList[index + 1];
}

const manifest = JSON.parse(read("manifest.json"));
const project = read("YuiToIssho.csproj");
const readme = read("README.md");
const saveData = read("src/Domain/SaveData.cs");
const migrator = read("src/Domain/SaveDataMigrator.cs");
const bootstrap = read("src/Hosting/Bootstrap.cs");
const multiplayer = read("src/Multiplayer/MultiplayerProtocol.cs");

const version = manifest.Version;
ensure(/^\d+\.\d+\.\d+$/.test(version), `Manifest version '${version}' is not semantic x.y.z.`);
ensure(capture(project, /<Version>([^<]+)<\/Version>/, "project version") === version, "Project and manifest versions differ.");
ensure(capture(project, /<AssemblyVersion>([^<]+)<\/AssemblyVersion>/, "assembly version") === `${version}.0`, "AssemblyVersion must be the release version plus .0.");
ensure(capture(project, /<FileVersion>([^<]+)<\/FileVersion>/, "file version") === `${version}.0`, "FileVersion must be the release version plus .0.");
ensure(capture(project, /<InformationalVersion>([^<]+)<\/InformationalVersion>/, "informational version") === version, "InformationalVersion must match the manifest.");
ensure(capture(readme, /img\.shields\.io\/badge\/version-([\d.]+)-/, "README version badge") === version, "README version badge is stale.");
ensure(capture(readme, /当前版本为 `([\d.]+)`/, "README current version") === version, "README current version is stale.");
ensure(manifest.EntryDll === "YuiToIssho.dll", "Manifest EntryDll must match the project assembly name.");

const currentSchema = Number(capture(saveData, /CurrentSchemaVersion\s*=\s*(\d+)/, "current save schema"));
const minimumSchema = Number(capture(migrator, /MinimumSupportedSchemaVersion\s*=\s*(\d+)/, "minimum save schema"));
const primarySchema = Number(capture(bootstrap, /SaveDataKey\s*=\s*"schema-v(\d+)"/, "primary save key"));
ensure(primarySchema === currentSchema, "Primary save key does not match CurrentSchemaVersion.");
ensure(minimumSchema <= currentSchema, "Minimum save schema exceeds the current schema.");

const persistedSchemas = new Set([...bootstrap.matchAll(/schema-v(\d+)/g)].map(match => Number(match[1])));
for (let schema = minimumSchema; schema <= currentSchema; schema += 1)
    ensure(persistedSchemas.has(schema), `Save key schema-v${schema} is missing.`);
for (let schema = minimumSchema; schema < currentSchema; schema += 1)
    ensure(new RegExp(`case\\s+${schema}\\s*:`).test(migrator), `Migration from schema v${schema} is missing.`);

const protocolVersion = Number(capture(multiplayer, /class MultiplayerProtocol[\s\S]*?Version\s*=\s*(\d+)/, "multiplayer protocol version"));
const messageTypes = [...multiplayer.matchAll(/public const string \w+ = "(r\d+\.[^"]+\.v\d+)";/g)].map(match => match[1]);
ensure(messageTypes.length > 0, "No multiplayer message types were found.");
for (const messageType of messageTypes)
    ensure(messageType.startsWith(`r${protocolVersion}.`) && messageType.endsWith(`.v${protocolVersion}`), `Message type '${messageType}' does not match protocol ${protocolVersion}.`);

const defaultTranslations = JSON.parse(read("i18n/default.json"));
const chineseTranslations = JSON.parse(read("i18n/zh.json"));
const defaultKeys = Object.keys(defaultTranslations).sort();
const chineseKeys = Object.keys(chineseTranslations).sort();
ensure(defaultKeys.length === chineseKeys.length && defaultKeys.every((key, index) => key === chineseKeys[index]), "Default and Chinese translation keys differ.");

const releaseTag = optionValue("--tag") ?? process.env.RELEASE_TAG;
if (releaseTag)
    ensure(releaseTag === `v${version}`, `Release tag '${releaseTag}' must be v${version}.`);

if (argumentsList.includes("--artifact")) {
    const artifact = path.join(root, "bin", "Release", "net6.0", `YuiToIssho ${version}.zip`);
    ensure(fs.existsSync(artifact), `Release artifact is missing: ${path.relative(root, artifact)}`);
}

console.log(`Release metadata is consistent: v${version}, schema-v${currentSchema}, protocol ${protocolVersion}, ${defaultKeys.length} translation keys.`);
