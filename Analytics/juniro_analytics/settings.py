"""Chargement de la configuration et de la chaîne de connexion.

La chaîne de connexion n'est jamais recopiée : elle est relue dans le appsettings du
service (même source de vérité que le scraper) puis convertie au format ODBC.
Ordre de priorité : option --connection, variable JUNIRO_ODBC, fichiers appsettings
(ceux de local.json d'abord, écrit par install-analytics.ps1 pour ce PC, puis ceux de config.json).

Appelé par : __main__.main au démarrage de chaque commande.
"""
from __future__ import annotations

import copy
import json
import os
import re
from pathlib import Path

ANALYTICS_DIR = Path(__file__).resolve().parent.parent
DEFAULT_CONFIG = ANALYTICS_DIR / "config.json"


def load_config(path: str | os.PathLike | None = None, overrides: dict | None = None) -> dict:
    """Lit config.json, applique tuned.json (réglage des modèles) puis les options de la ligne de commande."""
    cfg_path = Path(path) if path else DEFAULT_CONFIG
    with open(cfg_path, encoding="utf-8") as f:
        cfg = json.load(f)
    cfg["_path"] = str(cfg_path.resolve())
    tuned = cfg_path.parent / "tuned.json"     # écrit par la commande tune (réglage des modèles)
    cfg["_tuned"] = False
    if tuned.exists():
        with open(tuned, encoding="utf-8-sig") as f:
            t = {k: v for k, v in json.load(f).items() if not k.startswith("_")}
        cfg = deep_merge(cfg, t)
        cfg["_tuned"] = True
    if overrides:
        cfg = deep_merge(cfg, overrides)
    return cfg


def deep_merge(base: dict, extra: dict) -> dict:
    """Fusionne deux dictionnaires imbriqués (les valeurs de `extra` l'emportent)."""
    out = copy.deepcopy(base)
    for k, v in extra.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = deep_merge(out[k], v)
        else:
            out[k] = copy.deepcopy(v)
    return out


# Correspondance des mots-clés ADO.NET (Microsoft.Data.SqlClient) vers ODBC.
_ADO_TO_ODBC = {
    "server": "SERVER", "data source": "SERVER", "address": "SERVER", "addr": "SERVER",
    "database": "DATABASE", "initial catalog": "DATABASE",
    "user id": "UID", "uid": "UID", "user": "UID",
    "password": "PWD", "pwd": "PWD",
    "trustservercertificate": "TrustServerCertificate", "trust server certificate": "TrustServerCertificate",
    "encrypt": "Encrypt",
    "connect timeout": "Connection Timeout", "connection timeout": "Connection Timeout",
}


def ado_to_odbc(ado: str, driver: str) -> str:
    """Convertit 'Server=x;Database=y;User Id=u;Password=p;TrustServerCertificate=True' en chaîne ODBC."""
    parts = {}
    for chunk in ado.split(";"):
        if "=" not in chunk:
            continue
        key, value = chunk.split("=", 1)
        parts[key.strip().lower()] = value.strip()

    odbc = {"DRIVER": "{" + driver + "}"}
    for key, value in parts.items():
        if key in ("integrated security", "trusted_connection") and value.lower() in ("true", "sspi", "yes"):
            odbc["Trusted_Connection"] = "yes"
            continue
        target = _ADO_TO_ODBC.get(key)
        if not target:
            continue
        if value.lower() in ("true", "false"):
            value = "yes" if value.lower() == "true" else "no"
        if target == "SERVER":
            value = re.sub(r"^tcp:", "", value, flags=re.I)
        if target == "PWD" and (";" in value or "}" in value or value.startswith("{")):
            value = "{" + value.replace("}", "}}") + "}"
        odbc[target] = value
    return ";".join(f"{k}={v}" for k, v in odbc.items())


def load_appsettings(path) -> dict:
    """Lit un appsettings*.json avec la même tolérance que .NET : commentaires // et /* */, et
    virgule en trop avant } ou ] (le service démarre avec, json.load refuserait le fichier)."""
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':                                   # chaîne : recopiée telle quelle
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1]); i = j + 1
        elif text.startswith("//", i):                 # commentaire de fin de ligne
            j = text.find("\n", i); i = n if j < 0 else j
        elif text.startswith("/*", i):                 # commentaire de bloc
            j = text.find("*/", i + 2); i = n if j < 0 else j + 2
        elif c == ",":                                 # virgule suivie de } ou ] : supprimée
            j = i + 1
            while j < n and text[j] in " \t\r\n":
                j += 1
            if j < n and text[j] in "}]":
                i += 1
            else:
                out.append(c); i += 1
        else:
            out.append(c); i += 1
    try:
        return json.loads("".join(out))
    except json.JSONDecodeError as e:
        raise RuntimeError(f"{path} illisible : {e.msg}, ligne {e.lineno}, colonne {e.colno}. "
                           "Corrigez le fichier (guillemet ou virgule manquants ?).") from e


def resolve_connection(cfg: dict, explicit: str | None = None) -> str:
    """Trouve la chaîne de connexion : --connection, variable JUNIRO_ODBC, local.json, puis config.json."""
    if explicit:
        return explicit if "DRIVER=" in explicit.upper() else ado_to_odbc(explicit, cfg["database"]["odbc_driver"])
    env = os.environ.get("JUNIRO_ODBC")
    if env:
        return env

    db = cfg["database"]
    base = Path(cfg.get("_path", DEFAULT_CONFIG)).parent
    candidates = list(db["appsettings_files"])
    local = base / "local.json"   # propre à ce PC, écrit par install-analytics.ps1 (un chemin, aucun secret)
    if local.exists():
        with open(local, encoding="utf-8-sig") as f:
            extra = json.load(f).get("appsettings_files") or []
            candidates = ([extra] if isinstance(extra, str) else list(extra)) + candidates
    tried = []
    for rel in candidates:
        p = (base / rel).resolve()
        tried.append(str(p))
        if not p.exists():
            continue
        data = load_appsettings(p)
        ado = (data.get("ConnectionStrings") or {}).get(db["connection_name"])
        if ado:
            return ado_to_odbc(ado, db["odbc_driver"])
    raise RuntimeError(
        "Chaîne de connexion introuvable. Fichiers cherchés : " + " ; ".join(tried) +
        ". Relancez install-analytics.ps1 (il note l'emplacement du appsettings de ce PC), "
        "ou passez --connection, ou définissez JUNIRO_ODBC.")
