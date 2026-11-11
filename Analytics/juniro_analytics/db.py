"""Accès base de données, volontairement minimal (DB-API pur, sans ORM).

Fonctionne avec pyodbc (SQL Server, en production) et sqlite3 (tests). Les deux
utilisent des paramètres '?', ce qui permet de tester toute la chaîne d'écriture ici.

Appelé par : __main__.open_db, store.py. Aucune logique métier ici.
"""
from __future__ import annotations

import datetime as dt
import math
import re

import numpy as np
import pandas as pd


def choose_odbc_driver(conn_str: str, installed: list[str]) -> str:
    """Remplace le pilote demandé (DRIVER={...}) par un pilote SQL Server réellement installé
    s'il est absent : config.json demande le 18, certains PC n'ont que le 17."""
    m = re.search(r"DRIVER=\{([^}]*)\}", conn_str, flags=re.IGNORECASE)
    if not m or m.group(1) in installed:
        return conn_str
    for candidate in ("ODBC Driver 18 for SQL Server", "ODBC Driver 17 for SQL Server"):
        if candidate in installed:
            return conn_str[:m.start(1)] + candidate + conn_str[m.end(1):]
    return conn_str          # aucun pilote moderne : pyodbc donnera un message explicite


class Database:
    """Connexion à la base, avec lecture en DataFrame et écriture en bloc."""
    def __init__(self, conn, dialect: str):
        """Garde la connexion ouverte et le dialecte ('mssql' ou 'sqlite')."""
        self.conn = conn
        self.dialect = dialect  # 'mssql' ou 'sqlite'

    # ------------------------------------------------------------------ ouverture
    @classmethod
    def sqlserver(cls, odbc_connection_string: str) -> "Database":
        """Connexion SQL Server via le pilote ODBC (pyodbc)."""
        import pyodbc  # importé ici : inutile pour les tests hors ligne
        conn = pyodbc.connect(choose_odbc_driver(odbc_connection_string, pyodbc.drivers()), autocommit=False)
        return cls(conn, "mssql")

    @classmethod
    def sqlite(cls, path: str = ":memory:", schemas=("core", "feat", "ml", "sim", "app")) -> "Database":
        """Base SQLite en mémoire avec les schémas core/feat/ml/sim/app (tests et démo)."""
        import sqlite3
        sqlite3.register_adapter(dt.datetime, lambda d: d.isoformat(" "))
        sqlite3.register_adapter(dt.date, lambda d: d.isoformat())
        conn = sqlite3.connect(path)
        for s in schemas:
            conn.execute(f"ATTACH DATABASE ':memory:' AS {s}")
        return cls(conn, "sqlite")

    def close(self):
        """Ferme la connexion."""
        self.conn.close()

    # ------------------------------------------------------------------ lecture
    def read(self, sql: str, params=()) -> pd.DataFrame:
        """Exécute une requête et renvoie le résultat sous forme de DataFrame."""
        cur = self.conn.cursor()
        cur.execute(sql, params)
        cols = [c[0] for c in cur.description]
        rows = cur.fetchall()
        return pd.DataFrame.from_records([tuple(r) for r in rows], columns=cols)

    def scalar(self, sql: str, params=()):
        """Exécute une requête et renvoie la première valeur de la première ligne."""
        cur = self.conn.cursor()
        cur.execute(sql, params)
        row = cur.fetchone()
        return None if row is None else row[0]

    def execute(self, sql: str, params=()):
        """Exécute une requête sans résultat (INSERT, DELETE, CREATE...)."""
        cur = self.conn.cursor()
        cur.execute(sql, params)
        return cur

    def commit(self):
        """Valide la transaction en cours."""
        self.conn.commit()

    def table_exists(self, schema: str, table: str) -> bool:
        """Vrai si la table schema.table existe."""
        if self.dialect == "mssql":
            return self.scalar("SELECT OBJECT_ID(?)", (f"{schema}.{table}",)) is not None
        return self.scalar(f"SELECT name FROM {schema}.sqlite_master WHERE type='table' AND name=?", (table,)) is not None

    def columns(self, schema: str, table: str) -> set[str]:
        """Noms des colonnes de schema.table (ensemble vide si la table n'existe pas)."""
        if self.dialect == "mssql":
            df = self.read("SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(?)", (f"{schema}.{table}",))
            return set(df.iloc[:, 0]) if len(df) else set()
        try:
            df = self.read(f"PRAGMA {schema}.table_info({table})")
        except Exception:
            return set()
        return set(df["name"]) if len(df) else set()

    # ------------------------------------------------------------------ écriture
    def insert_returning_id(self, table: str, row: dict, id_col: str) -> int:
        """Insère une ligne et renvoie l'identifiant généré (RunId, SimRunId...)."""
        cols = list(row)
        marks = ",".join("?" for _ in cols)
        values = tuple(_py(row[c]) for c in cols)
        col_list = ",".join(cols)
        if self.dialect == "mssql":
            sql = f"INSERT INTO {table} ({col_list}) OUTPUT INSERTED.{id_col} VALUES ({marks})"
        else:
            sql = f"INSERT INTO {table} ({col_list}) VALUES ({marks}) RETURNING {id_col}"
        cur = self.conn.cursor()
        cur.execute(sql, values)
        new_id = int(cur.fetchone()[0])
        return new_id

    def append(self, table: str, df: pd.DataFrame, chunk: int = 2000):
        """Insère un DataFrame dans une table existante (colonnes = noms du DataFrame)."""
        if df.empty:
            return 0
        cols = list(df.columns)
        sql = f"INSERT INTO {table} ({','.join(_q(c, self.dialect) for c in cols)}) VALUES ({','.join('?' for _ in cols)})"
        cur = self.conn.cursor()
        if self.dialect == "mssql":
            cur.fast_executemany = True
        records = _records(df)
        for i in range(0, len(records), chunk):
            cur.executemany(sql, records[i:i + chunk])
        return len(records)

    def replace_table(self, schema: str, table: str, df: pd.DataFrame, key: list[str] | None = None):
        """Recrée entièrement une table de variables (reconstruction complète à chaque passage)."""
        full = f"{schema}.{table}"
        if self.dialect == "mssql":
            self.execute(f"IF OBJECT_ID('{full}') IS NOT NULL DROP TABLE {full}")
        else:
            self.execute(f"DROP TABLE IF EXISTS {full}")
        col_defs = ",\n  ".join(f"{_q(c, self.dialect)} {_sql_type(df[c], self.dialect, key and c in key)}"
                                for c in df.columns)
        pk = ""
        if key:
            pk = f",\n  CONSTRAINT PK_{schema}_{table} PRIMARY KEY ({','.join(_q(k, self.dialect) for k in key)})"
        self.execute(f"CREATE TABLE {full} (\n  {col_defs}{pk}\n)")
        self.append(full, df)


def _q(name: str, dialect: str) -> str:
    """Met un nom de colonne entre crochets (SQL Server) ou guillemets (SQLite)."""
    return f"[{name}]" if dialect == "mssql" else f'"{name}"'


def _sql_type(s: pd.Series, dialect: str, not_null: bool = False) -> str:
    """Type SQL d'une colonne pandas (FLOAT, BIGINT, DATETIME2, NVARCHAR...)."""
    nn = " NOT NULL" if not_null else " NULL"
    if pd.api.types.is_bool_dtype(s):
        t = "BIT" if dialect == "mssql" else "INTEGER"
    elif pd.api.types.is_integer_dtype(s):
        t = "BIGINT" if dialect == "mssql" else "INTEGER"
    elif pd.api.types.is_float_dtype(s):
        t = "FLOAT" if dialect == "mssql" else "REAL"
    elif pd.api.types.is_datetime64_any_dtype(s):
        t = "DATETIME2(0)" if dialect == "mssql" else "TIMESTAMP"
    else:
        width = int(s.astype(str).str.len().max() or 1) if len(s) else 1
        t = f"NVARCHAR({max(20, min(4000, width * 2))})" if dialect == "mssql" else "TEXT"
    return t + nn


def _py(v):
    """Convertit une valeur numpy/pandas en type Python accepté par le pilote."""
    if v is None:
        return None
    if isinstance(v, (float, np.floating)):
        f = float(v)
        return None if math.isnan(f) or math.isinf(f) else f
    if isinstance(v, (np.integer,)):
        return int(v)
    if isinstance(v, (np.bool_,)):
        return bool(v)
    if isinstance(v, pd.Timestamp):
        return None if pd.isna(v) else v.to_pydatetime().replace(tzinfo=None)
    if isinstance(v, dt.date) and not isinstance(v, dt.datetime):
        return v
    if v is pd.NaT:
        return None
    try:
        if pd.isna(v):
            return None
    except (TypeError, ValueError):
        pass
    return v


def _records(df: pd.DataFrame) -> list[tuple]:
    """Transforme un DataFrame en liste de tuples prêts pour executemany (NaN -> NULL)."""
    cols = []
    for c in df.columns:
        s = df[c]
        if pd.api.types.is_datetime64_any_dtype(s):
            cols.append([None if pd.isna(x) else x.to_pydatetime() for x in s])
        elif pd.api.types.is_float_dtype(s):
            arr = s.to_numpy(dtype=float)
            cols.append([None if not np.isfinite(x) else float(x) for x in arr])
        else:
            cols.append([_py(x) for x in s.to_numpy(dtype=object)])
    return list(zip(*cols))
