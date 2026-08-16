namespace Ironsight

[<RequireQualifiedAccess>]
module Damage =
    let hurtPlayer damage fromDirection (player: Player) =
        let health = max (Units.health 0.0f) (player.Health - damage)
        { player with Health = health; RegenIn = Tuning.RegenDelay }, PlayerHurt(fromDirection, health)

    let stepRegen (dt: float32<s>) (player: Player) =
        if player.Health <= Units.health 0.0f then player
        elif player.RegenIn > Units.seconds 0.0f then
            { player with RegenIn = max (Units.seconds 0.0f) (player.RegenIn - dt) }
        else
            let healed = Tuning.RegenPerSecond * (dt / Units.seconds 1.0f)
            { player with Health = min (Units.health 100.0f) (player.Health + healed) }
