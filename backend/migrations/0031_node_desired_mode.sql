-- Mode de fonctionnement voulu d'un nœud (architecture 0036) : demandé par un administrateur, appliqué par le nœud
-- à son prochain signal de présence, puis effacé quand les rôles déclarés correspondent.
ALTER TABLE cluster_nodes
    ADD COLUMN desired_roles text[] CHECK (desired_roles IS NULL OR (cardinality(desired_roles) BETWEEN 1 AND 2 AND desired_roles <@ ARRAY['server', 'worker']));
