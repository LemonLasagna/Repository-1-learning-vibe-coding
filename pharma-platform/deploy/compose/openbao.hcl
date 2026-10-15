# OpenBao single-node (tier S). Integrated Raft storage on the encrypted data disk.
storage "raft" {
  path    = "/openbao/data"
  node_id = "pharmaos-1"
}
listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true            # internal Docker network only; enable TLS for tier M/L
}
api_addr      = "http://openbao:8200"
cluster_addr  = "http://openbao:8201"
ui            = false
disable_mlock = false
