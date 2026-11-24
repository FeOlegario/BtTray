# Checagens antes de publicar, rodadas pelo CI (build.yml) e antes de criar uma Release (release.yml).
#
# 1. Notas da versão: a versão em src/Program.cs precisa ter release-notes/vX.Y.Z.md, com as seções em inglês e em
#    português. Com -Tag, confere também que a tag bate com a versão.
# 2. Dados privados: nenhum arquivo do repositório nem mensagem de commit pode conter os termos da variável de
#    ambiente TERMOS_PRIVADOS (um por linha; vem de um secret do GitHub, nunca do repositório) nem endereços
#    Bluetooth no formato XX:XX:XX:XX:XX:XX que não sejam claramente de exemplo.
#
# Uso:  .github/scripts/checar-publicacao.ps1 [-Tag v1.2.3] [-Commits <intervalo git, ex.: base..head>]
param(
    [string]$Tag = '',
    [string]$Commits = ''
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..\..')
$falhas = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------- 1. versão e notas
$program = Get-Content src\Program.cs -Raw
$m = [regex]::Match($program, 'AssemblyFileVersion\("(\d+)\.(\d+)\.(\d+)\.\d+"\)')
if (-not $m.Success) {
    $falhas.Add('Não encontrei AssemblyFileVersion em src/Program.cs.')
} else {
    $versao = 'v{0}.{1}.{2}' -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value
    Write-Host "Versão em src/Program.cs: $versao"
    if ($Tag -and $Tag -ne $versao) {
        $falhas.Add("A tag $Tag não bate com a versão $versao de src/Program.cs.")
    }
    $notas = "release-notes\$versao.md"
    if (-not (Test-Path $notas)) {
        $falhas.Add("Falta $($notas -replace '\\','/'): toda versão nova precisa das notas em inglês e em português.")
    } else {
        $texto = [IO.File]::ReadAllText((Resolve-Path $notas), [Text.Encoding]::UTF8)
        if ($texto -notmatch '(?m)^##\s.*English') { $falhas.Add("$notas não tem a seção '## ... English'.") }
        if ($texto -notmatch '(?m)^##\s.*Portugu') { $falhas.Add("$notas não tem a seção '## ... Português'.") }
        if ($texto.Trim().Length -lt 80) { $falhas.Add("$notas parece vazio.") }
        Write-Host "Notas encontradas: $notas"
    }
}

# ---------------------------------------------------------------- 2. dados privados
# arquivos de texto versionados (imagens e binários ficam de fora: lá a checagem tem de ser visual)
$binarios = '\.(png|jpe?g|gif|ico|exe|dll|pdb|zip)$'
$arquivos = git ls-files | Where-Object { $_ -notmatch $binarios -and (Test-Path $_) }

# mensagens dos commits do intervalo (ex.: os commits de um PR), uma entrada por commit
$mensagens = @()
if ($Commits) {
    $hashes = git log --format=%h $Commits 2>$null
    if ($LASTEXITCODE -eq 0 -and $hashes) {
        foreach ($h in @($hashes)) { $mensagens += ,@($h, @(git log -1 --format=%B $h)) }
    }
}

$termos = @()
if ($env:TERMOS_PRIVADOS) {
    $termos = @($env:TERMOS_PRIVADOS -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -ge 3 })
}
if ($termos.Count -eq 0) {
    # forks e PRs de forks não recebem secrets: avisa, mas não falha
    Write-Host '::warning::TERMOS_PRIVADOS não está configurado; a busca por nomes de dispositivos foi pulada.'
} else {
    Write-Host "Procurando $($termos.Count) termo(s) privado(s) em $(@($arquivos).Count) arquivo(s) e $($mensagens.Count) commit(s)."
}

# endereço Bluetooth com separadores; os de exemplo (todos iguais, sequências 00..05/11..66, AA:BB:CC...) passam
$reEndereco = [regex]'\b([0-9A-Fa-f]{2})([:-])([0-9A-Fa-f]{2})\2([0-9A-Fa-f]{2})\2([0-9A-Fa-f]{2})\2([0-9A-Fa-f]{2})\2([0-9A-Fa-f]{2})\b'
function Test-EnderecoDeExemplo([string]$e) {
    $o = ($e.ToUpper() -replace '[:-]', '')
    if ($o -match '^(.)\1*$') { return $true }                                     # 000000000000, FFFFFFFFFFFF
    if ($o -match '^((..)\2{5})$') { return $true }                                # AAAAAAAAAAAA, 121212121212
    if ('AABBCCDDEEFF', '001122334455', '112233445566', '012345678901', '123456789ABC' -contains $o) { return $true }
    return $false
}

function Find-Privado([string]$onde, [string[]]$linhas) {
    for ($i = 0; $i -lt $linhas.Count; $i++) {
        $l = $linhas[$i]
        foreach ($t in $termos) {
            if ($l.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                # o termo nunca vai para o log: só onde ele está
                $falhas.Add("Nome/termo privado em ${onde}:$($i + 1).")
                break
            }
        }
        foreach ($mm in $reEndereco.Matches($l)) {
            if (-not (Test-EnderecoDeExemplo $mm.Value)) {
                $falhas.Add("Endereço Bluetooth que parece real em ${onde}:$($i + 1). Use um de exemplo (ex.: AA:BB:CC:DD:EE:FF).")
            }
        }
    }
}

foreach ($a in $arquivos) {
    $linhas = [IO.File]::ReadAllLines((Resolve-Path $a), [Text.Encoding]::UTF8)
    Find-Privado ($a -replace '\\', '/') $linhas
}
foreach ($c in $mensagens) {
    Find-Privado "mensagem do commit $($c[0])" @($c[1])
}

# ---------------------------------------------------------------- resultado
if ($falhas.Count -gt 0) {
    Write-Host ''
    foreach ($f in $falhas) { Write-Host "::error::$f" }
    throw "$($falhas.Count) problema(s) antes da publicação (veja acima)."
}
Write-Host 'Checagens de publicação: tudo certo.'
